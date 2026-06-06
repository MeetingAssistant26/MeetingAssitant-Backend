using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Hangfire;
using MeetingAssistant.Features.ActionItems.Jobs;
using MeetingAssistant.Features.ActionItems.Models.Entities;
using MeetingAssistant.Features.ActionItems.Models.Enums;
using MeetingAssistant.Features.AgentApi.Services;
using MeetingAssistant.Features.Identity.Entites;
using MeetingAssistant.Features.Identity.Services;
using MeetingAssistant.Features.LiveSession.Jobs;
using MeetingAssistant.Features.LiveSession.Models;
using MeetingAssistant.Features.LiveSession.Models.PostProcessing;
using MeetingAssistant.Features.LiveSession.Services;
using MeetingAssistant.Features.LiveSession.Services.PostProcessing;
using MeetingAssistant.Features.Meetings.Models;
using MeetingAssistant.Features.Organizations.Models;
using MeetingAssistant.Features.Rag.Jobs;
using MeetingAssistant.Features.Rag.Models;
using MeetingAssistant.Infrastructure.Persistence.DbContext;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MeetingAssistant.Features.DevQa;

[ApiController]
[AllowAnonymous]
[Route("api/dev/qa")]
public sealed class DevQaController(
    ApplicationDbContext dbContext,
    UserManager<ApplicationUser> userManager,
    ITokenService tokenService,
    ILiveKitTokenIssuer liveKitTokenIssuer,
    IStorageService storageService,
    IAgentAuthService agentAuthService,
    IBackgroundJobClient backgroundJobClient,
    IServiceProvider serviceProvider,
    IQaSttFailureInjectionService qaSttFailureInjectionService,
    IMeetingTranscriptPreviewService meetingTranscriptPreviewService,
    IHostEnvironment environment,
    IConfiguration configuration,
    ILogger<DevQaController> logger) : ControllerBase
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private const int MaxOrganizationSlugLength = 100;
    private const int MaxOrganizationNameLength = 150;
    private const int MaxMeetingTitleLength = 200;
    private const int MaxMeetingTagNameLength = 50;
    private const int MaxGeneratedEmailLength = 100;
    private const int MaxGeneratedDisplayNameLength = 150;
    private const int MaxMeetingDescriptionLength = 2000;
    private const string GeneratedEmailDomain = "@meetingassistant.local";

    private readonly ApplicationDbContext _dbContext = dbContext;
    private readonly UserManager<ApplicationUser> _userManager = userManager;
    private readonly ITokenService _tokenService = tokenService;
    private readonly ILiveKitTokenIssuer _liveKitTokenIssuer = liveKitTokenIssuer;
    private readonly IStorageService _storageService = storageService;
    private readonly IAgentAuthService _agentAuthService = agentAuthService;
    private readonly IBackgroundJobClient _backgroundJobClient = backgroundJobClient;
    private readonly IServiceProvider _serviceProvider = serviceProvider;
    private readonly IQaSttFailureInjectionService _qaSttFailureInjectionService = qaSttFailureInjectionService;
    private readonly IMeetingTranscriptPreviewService _meetingTranscriptPreviewService = meetingTranscriptPreviewService;
    private readonly IHostEnvironment _environment = environment;
    private readonly IConfiguration _configuration = configuration;
    private readonly ILogger<DevQaController> _logger = logger;

    [HttpPost("scenario")]
    [ProducesResponseType(typeof(QaScenarioResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> CreateScenario(
        [FromBody] QaScenarioRequest? request,
        CancellationToken cancellationToken)
    {
        if (!IsQaHarnessEnabled())
            return NotFound();

        request ??= new QaScenarioRequest();

        var runId = string.IsNullOrWhiteSpace(request.RunId)
            ? DateTime.UtcNow.ToString("yyyyMMddHHmmssfff")
            : Slugify(request.RunId!);

        var scenarioName = string.IsNullOrWhiteSpace(request.Scenario)
            ? "qa-scenario"
            : request.Scenario!.Trim();
        var scenarioSlug = Slugify(scenarioName);
        var orgSlug = BuildQaOrganizationSlug(request.OrganizationSlug ?? $"{scenarioSlug}-{runId}");
        var orgName = TruncateForStorage(
            string.IsNullOrWhiteSpace(request.OrganizationName)
                ? $"QA {scenarioName} {runId}"
                : request.OrganizationName!,
            MaxOrganizationNameLength);

        var requestedUsers = request.Users is { Count: > 0 }
            ? request.Users
            :
            [
                new QaScenarioUserRequest("alice", "QA Alice", OrganizationRole.Admin, MeetingRole.Host, null),
                new QaScenarioUserRequest("bob", "QA Bob", OrganizationRole.Member, MeetingRole.Participant, null)
            ];

        var duplicateLabels = requestedUsers
            .GroupBy(user => NormalizeLabel(user.Label))
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToList();
        if (duplicateLabels.Count > 0)
            return BadRequest(new { error = "Duplicate user labels are not allowed.", duplicateLabels });

        var organization = new Organization
        {
            Name = orgName,
            Slug = orgSlug
        };
        _dbContext.Organizations.Add(organization);

        var createdUsers = new List<QaScenarioUserInternal>();
        foreach (var requestedUser in requestedUsers)
        {
            var label = NormalizeLabel(requestedUser.Label);
            var email = BuildQaGeneratedEmail(scenarioSlug, runId, label);
            var displayName = TruncateForStorage(
                string.IsNullOrWhiteSpace(requestedUser.DisplayName)
                    ? $"QA {label}"
                    : requestedUser.DisplayName!,
                MaxGeneratedDisplayNameLength);
            var password = string.IsNullOrWhiteSpace(requestedUser.Password)
                ? "Password#123"
                : requestedUser.Password!;

            var user = new ApplicationUser
            {
                Id = Guid.NewGuid(),
                UserName = email,
                NormalizedUserName = email.ToUpperInvariant(),
                Email = email,
                NormalizedEmail = email.ToUpperInvariant(),
                DisplayName = displayName,
                EmailConfirmed = true,
                SecurityStamp = Guid.NewGuid().ToString(),
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow
            };

            var createResult = await _userManager.CreateAsync(user, password);
            if (!createResult.Succeeded)
            {
                var errors = createResult.Errors.Select(error => error.Description).ToList();
                return BadRequest(new { error = "Failed to create QA user.", label, email, errors });
            }

            createdUsers.Add(new QaScenarioUserInternal(
                label,
                user,
                requestedUser.OrganizationRole ?? OrganizationRole.Member,
                requestedUser.MeetingRole ?? MeetingRole.Participant,
                password));
        }

        if (!createdUsers.Any(user => user.MeetingRole == MeetingRole.Host))
        {
            createdUsers[0] = createdUsers[0] with { MeetingRole = MeetingRole.Host };
        }

        foreach (var createdUser in createdUsers)
        {
            _dbContext.UserOrgMemberships.Add(new UserOrgMembership
            {
                UserId = createdUser.User.Id,
                OrganizationId = organization.Id,
                OrgRole = createdUser.OrganizationRole,
                IsEnabled = true
            });
        }

        var tagsByName = new Dictionary<string, MeetingTag>(StringComparer.OrdinalIgnoreCase);
        foreach (var tagName in request.Tags ?? [])
        {
            if (string.IsNullOrWhiteSpace(tagName))
                continue;

            var normalizedTagName = TruncateForStorage(tagName, MaxMeetingTagNameLength);
            if (string.IsNullOrWhiteSpace(normalizedTagName))
                continue;

            if (tagsByName.ContainsKey(normalizedTagName))
                continue;

            var tag = new MeetingTag
            {
                OrganizationId = organization.Id,
                Name = normalizedTagName,
                Color = "#6366f1",
                IsActive = true
            };
            tagsByName[normalizedTagName] = tag;
            _dbContext.MeetingTags.Add(tag);
        }

        var now = DateTime.UtcNow;
        var meetingRequest = request.Meeting ?? new QaScenarioMeetingRequest();
        var meeting = new Meeting
        {
            OrganizationId = organization.Id,
            Title = TruncateForStorage(
                string.IsNullOrWhiteSpace(meetingRequest.Title)
                    ? $"QA {scenarioName} {runId}"
                    : meetingRequest.Title!,
                MaxMeetingTitleLength),
            Description = TruncateForStorage(
                meetingRequest.Description ?? $"Autonomous QA scenario {scenarioName} created at {now:O}.",
                MaxMeetingDescriptionLength),
            ScheduledStartUtc = meetingRequest.ScheduledStartUtc ?? now.AddMinutes(-5),
            ScheduledEndUtc = meetingRequest.ScheduledEndUtc ?? now.AddHours(1),
            Status = meetingRequest.Status ?? MeetingStatus.Scheduled,
            AiAssistantEnabled = meetingRequest.AiAssistantEnabled ?? true,
            RoomActivatedAtUtc = meetingRequest.Status == MeetingStatus.InProgress ? now : null
        };

        foreach (var createdUser in createdUsers)
        {
            meeting.Participants.Add(new MeetingParticipant
            {
                OrganizationId = organization.Id,
                UserId = createdUser.User.Id,
                MeetingRole = createdUser.MeetingRole
            });
        }

        foreach (var tag in tagsByName.Values)
        {
            meeting.Tags.Add(new MeetingMeetingTag
            {
                Meeting = meeting,
                MeetingTag = tag
            });
        }

        _dbContext.Meetings.Add(meeting);
        await _dbContext.SaveChangesAsync(cancellationToken);

        var userResponses = new Dictionary<string, QaScenarioUserResponse>(StringComparer.OrdinalIgnoreCase);
        var joinTokens = new Dictionary<string, QaScenarioJoinTokenResponse>(StringComparer.OrdinalIgnoreCase);
        foreach (var createdUser in createdUsers)
        {
            var (accessToken, expiresIn) = _tokenService.GenerateAccessToken(
                createdUser.User,
                organization.Id,
                createdUser.OrganizationRole.ToString());

            userResponses[createdUser.Label] = new QaScenarioUserResponse(
                createdUser.User.Id,
                createdUser.User.Email!,
                createdUser.User.DisplayName ?? createdUser.User.Email!,
                createdUser.OrganizationRole.ToString(),
                createdUser.MeetingRole.ToString(),
                accessToken,
                expiresIn);

            var permissions = SessionPermissions.ForRole(createdUser.MeetingRole);
            var joinToken = _liveKitTokenIssuer.Issue(
                meeting.Id,
                organization.Id,
                createdUser.User.Id,
                createdUser.User.DisplayName ?? createdUser.User.Email!,
                createdUser.MeetingRole,
                permissions);

            if (joinToken.IsSuccess)
            {
                joinTokens[createdUser.Label] = new QaScenarioJoinTokenResponse(
                    joinToken.Value.AccessToken,
                    joinToken.Value.RoomName,
                    joinToken.Value.ServerUrl,
                    joinToken.Value.ExpiresAtUtc,
                    permissions);
            }
        }

        return Ok(new QaScenarioResponse(
            scenarioName,
            runId,
            organization.Id,
            organization.Name,
            organization.Slug,
            meeting.Id,
            meeting.Title,
            $"mtg:{meeting.Id}",
            userResponses,
            joinTokens,
            tagsByName.Values.Select(tag => new QaScenarioTagResponse(tag.Id, tag.Name)).ToList()));
    }

    [HttpPost("organizations/{organizationId:guid}/meetings")]
    [ProducesResponseType(typeof(QaAdditionalMeetingResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> CreateAdditionalMeeting(
        [FromRoute] Guid organizationId,
        [FromBody] QaCreateAdditionalMeetingRequest? request,
        CancellationToken cancellationToken)
    {
        if (!IsQaHarnessEnabled())
            return NotFound();

        if (request is null)
            return BadRequest(new { error = "Request body is required." });

        if (request.SourceMeetingId == Guid.Empty)
            return BadRequest(new { error = "SourceMeetingId is required." });

        if (request.RecurringOccurrenceIndex is < 0)
            return BadRequest(new { error = "RecurringOccurrenceIndex must be non-negative." });

        var organizationExists = await _dbContext.Organizations
            .IgnoreQueryFilters()
            .AnyAsync(x => x.Id == organizationId, cancellationToken);
        if (!organizationExists)
            return NotFound(new { error = "Organization not found." });

        var sourceMeeting = await _dbContext.Meetings
            .IgnoreQueryFilters()
            .Include(x => x.Participants)
            .Include(x => x.Tags)
                .ThenInclude(x => x.MeetingTag)
            .FirstOrDefaultAsync(
                x => x.Id == request.SourceMeetingId && x.OrganizationId == organizationId,
                cancellationToken);

        if (sourceMeeting is null)
            return NotFound(new { error = "Source meeting not found for organization." });

        var copyParticipants = request.CopyParticipants ?? true;
        var copyTags = request.CopyTags ?? true;
        if (copyParticipants && sourceMeeting.Participants.Count == 0)
            return BadRequest(new { error = "Source meeting has no participants to copy." });

        var now = DateTime.UtcNow;
        var scheduledStartUtc = request.ScheduledStartUtc ?? now.AddMinutes(10);
        var status = request.Status ?? MeetingStatus.InProgress;
        var meeting = new Meeting
        {
            OrganizationId = organizationId,
            Title = TruncateForStorage(
                string.IsNullOrWhiteSpace(request.Title)
                    ? $"QA Follow-up {sourceMeeting.Title}"
                    : request.Title!,
                MaxMeetingTitleLength),
            Description = TruncateForStorage(
                string.IsNullOrWhiteSpace(request.Description)
                    ? $"Autonomous QA follow-up meeting for source meeting {sourceMeeting.Id}."
                    : request.Description!,
                MaxMeetingDescriptionLength),
            ScheduledStartUtc = scheduledStartUtc,
            ScheduledEndUtc = request.ScheduledEndUtc ?? scheduledStartUtc.AddHours(1),
            Status = status,
            AiAssistantEnabled = request.AiAssistantEnabled ?? sourceMeeting.AiAssistantEnabled,
            RoomActivatedAtUtc = status == MeetingStatus.InProgress ? now : null
        };

        if (copyParticipants)
        {
            foreach (var participant in sourceMeeting.Participants)
            {
                meeting.Participants.Add(new MeetingParticipant
                {
                    OrganizationId = organizationId,
                    UserId = participant.UserId,
                    MeetingRole = participant.MeetingRole
                });
            }
        }

        if (copyTags)
        {
            foreach (var tag in sourceMeeting.Tags)
            {
                meeting.Tags.Add(new MeetingMeetingTag
                {
                    Meeting = meeting,
                    MeetingTagId = tag.MeetingTagId
                });
            }
        }

        if (request.LinkRecurringSeries == true)
        {
            var seriesId = sourceMeeting.RecurringSeriesId;
            if (!seriesId.HasValue)
            {
                var createdByUserId = sourceMeeting.Participants
                    .Where(participant => participant.MeetingRole == MeetingRole.Host)
                    .Select(participant => participant.UserId)
                    .FirstOrDefault();

                if (createdByUserId == Guid.Empty)
                {
                    createdByUserId = sourceMeeting.Participants
                        .Select(participant => participant.UserId)
                        .FirstOrDefault();
                }

                if (createdByUserId == Guid.Empty)
                    return BadRequest(new { error = "Source meeting has no participant to own recurring series." });

                var series = new RecurringMeetingSeries
                {
                    OrganizationId = organizationId,
                    Title = sourceMeeting.Title,
                    Description = sourceMeeting.Description,
                    ScheduledStartTimeUtc = sourceMeeting.ScheduledStartUtc.TimeOfDay,
                    ScheduledEndTimeUtc = sourceMeeting.ScheduledEndUtc.TimeOfDay,
                    Frequency = RecurrenceFrequency.Weekly,
                    Interval = 1,
                    DaysOfWeek = sourceMeeting.ScheduledStartUtc.DayOfWeek.ToString(),
                    Status = RecurringMeetingSeriesStatus.Active,
                    CreatedByUserId = createdByUserId
                };

                _dbContext.RecurringMeetingSeries.Add(series);
                sourceMeeting.RecurringSeriesId = series.Id;
                seriesId = series.Id;
            }

            sourceMeeting.RecurringOccurrenceIndex ??= 0;

            var maxExistingOccurrenceIndex = await _dbContext.Meetings
                .IgnoreQueryFilters()
                .Where(x => x.OrganizationId == organizationId
                            && x.RecurringSeriesId == seriesId
                            && x.RecurringOccurrenceIndex.HasValue)
                .MaxAsync(x => (int?)x.RecurringOccurrenceIndex, cancellationToken);

            var maxOccurrenceIndex = Math.Max(
                sourceMeeting.RecurringOccurrenceIndex ?? 0,
                maxExistingOccurrenceIndex ?? 0);
            var targetOccurrenceIndex = request.RecurringOccurrenceIndex ?? maxOccurrenceIndex + 1;

            var duplicateOccurrenceIndex = sourceMeeting.RecurringOccurrenceIndex == targetOccurrenceIndex
                                           || await _dbContext.Meetings
                                               .IgnoreQueryFilters()
                                               .AnyAsync(
                                                   x => x.OrganizationId == organizationId
                                                        && x.RecurringSeriesId == seriesId
                                                        && x.Id != sourceMeeting.Id
                                                        && x.RecurringOccurrenceIndex == targetOccurrenceIndex,
                                                   cancellationToken);

            if (duplicateOccurrenceIndex)
                return BadRequest(new { error = "RecurringOccurrenceIndex already exists in this series." });

            meeting.RecurringSeriesId = seriesId;
            meeting.RecurringOccurrenceIndex = targetOccurrenceIndex;
        }

        _dbContext.Meetings.Add(meeting);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return Ok(new QaAdditionalMeetingResponse(
            organizationId,
            sourceMeeting.Id,
            meeting.Id,
            meeting.Title,
            $"mtg:{meeting.Id}",
            meeting.Participants
                .Select(participant => new QaAdditionalMeetingParticipantResponse(
                    participant.Id,
                    participant.UserId,
                    participant.MeetingRole.ToString()))
                .ToList(),
            copyTags
                ? sourceMeeting.Tags
                    .Select(tag => new QaScenarioTagResponse(tag.MeetingTagId, tag.MeetingTag.Name))
                    .ToList()
                : [],
            meeting.ScheduledStartUtc,
            meeting.ScheduledEndUtc,
            meeting.RecurringSeriesId,
            meeting.RecurringOccurrenceIndex));
    }

    [HttpGet("organizations/{organizationId:guid}/reminders")]
    [ProducesResponseType(typeof(IReadOnlyList<QaReminderStatusResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetReminderStatuses(
        [FromRoute] Guid organizationId,
        [FromQuery] Guid? meetingId,
        [FromQuery] bool? includeSeries,
        CancellationToken cancellationToken)
    {
        if (!IsQaHarnessEnabled())
            return NotFound();

        var organizationExists = await _dbContext.Organizations
            .IgnoreQueryFilters()
            .AnyAsync(x => x.Id == organizationId, cancellationToken);
        if (!organizationExists)
            return NotFound(new { error = "Organization not found." });

        Meeting? meeting = null;
        if (meetingId.HasValue)
        {
            meeting = await _dbContext.Meetings
                .IgnoreQueryFilters()
                .AsNoTracking()
                .FirstOrDefaultAsync(
                    x => x.Id == meetingId.Value && x.OrganizationId == organizationId,
                    cancellationToken);
            if (meeting is null)
                return NotFound(new { error = "Meeting not found for organization." });
        }

        var query =
            from reminder in _dbContext.Reminders.IgnoreQueryFilters().AsNoTracking()
            where reminder.OrganizationId == organizationId
            join sourceMeeting in _dbContext.Meetings.IgnoreQueryFilters().AsNoTracking()
                on reminder.MeetingId equals sourceMeeting.Id into sourceMeetings
            from sourceMeeting in sourceMeetings.DefaultIfEmpty()
            select new { Reminder = reminder, SourceMeeting = sourceMeeting };

        if (meetingId.HasValue)
        {
            if (includeSeries == true && meeting!.RecurringSeriesId.HasValue)
            {
                query = query.Where(x => x.Reminder.MeetingId == meetingId.Value
                                         || (x.SourceMeeting != null
                                             && x.SourceMeeting.OrganizationId == organizationId
                                             && x.SourceMeeting.RecurringSeriesId == meeting.RecurringSeriesId));
            }
            else
            {
                query = query.Where(x => x.Reminder.MeetingId == meetingId.Value);
            }
        }

        var rows = await query
            .OrderBy(x => x.Reminder.ReminderAtUtc)
            .ThenBy(x => x.Reminder.CreatedAtUtc)
            .ToListAsync(cancellationToken);

        return Ok(rows
            .Select(x => new QaReminderStatusResponse(
                x.Reminder.Id,
                x.Reminder.OrganizationId,
                x.Reminder.MeetingId,
                x.Reminder.Text,
                x.Reminder.Scope.ToString(),
                x.Reminder.Channel.ToString(),
                x.Reminder.Status.ToString(),
                x.Reminder.ReminderAtUtc,
                x.Reminder.DeliveredAtUtc,
                x.Reminder.CreatedByUserId,
                x.Reminder.TargetUserId,
                x.SourceMeeting?.RecurringSeriesId,
                x.SourceMeeting?.RecurringOccurrenceIndex,
                x.SourceMeeting?.ScheduledStartUtc))
            .ToList());
    }

    [HttpPost("meetings/{meetingId:guid}/agent-token")]
    [ProducesResponseType(typeof(QaAgentTokenResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> MintAgentToken(
        [FromRoute] Guid meetingId,
        [FromBody] QaMintAgentTokenRequest? request,
        CancellationToken cancellationToken)
    {
        if (!IsQaHarnessEnabled())
            return NotFound();

        if (request is null)
            return BadRequest(new { error = "Request body is required." });

        if (request.OrganizationId == Guid.Empty)
            return BadRequest(new { error = "OrganizationId is required." });

        if (request.ExpiresInMinutes is < 1 or > 1440)
            return BadRequest(new { error = "ExpiresInMinutes must be between 1 and 1440." });

        var meetingExists = await _dbContext.Meetings
            .IgnoreQueryFilters()
            .AnyAsync(x => x.Id == meetingId && x.OrganizationId == request.OrganizationId, cancellationToken);
        if (!meetingExists)
            return NotFound(new { error = "Meeting not found for organization." });

        var expiresInMinutes = request.ExpiresInMinutes ?? 60;
        var lifetime = TimeSpan.FromMinutes(expiresInMinutes);
        var result = await _agentAuthService.MintTokenAsync(request.OrganizationId, meetingId, lifetime, cancellationToken);
        if (result.IsFailure)
        {
            return BadRequest(new
            {
                error = result.Error.Code,
                message = result.Error.Description
            });
        }

        return Ok(new QaAgentTokenResponse(
            request.OrganizationId,
            meetingId,
            "Bearer",
            result.Value,
            DateTime.UtcNow.Add(lifetime),
            expiresInMinutes));
    }

    [HttpPost("meetings/{meetingId:guid}/audio-fragments")]
    [RequestSizeLimit(100_000_000)]
    [Consumes("multipart/form-data")]
    [ProducesResponseType(typeof(QaAudioFragmentResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> UploadAudioFragment(
        [FromRoute] Guid meetingId,
        [FromForm] QaAudioFragmentUploadRequest request,
        CancellationToken cancellationToken)
    {
        if (!IsQaHarnessEnabled())
            return NotFound();

        if (request.File is null || request.File.Length == 0)
            return BadRequest(new { error = "A non-empty audio file is required." });

        var meeting = await _dbContext.Meetings
            .IgnoreQueryFilters()
            .Include(x => x.Participants)
            .FirstOrDefaultAsync(
                x => x.Id == meetingId && x.OrganizationId == request.OrganizationId,
                cancellationToken);

        if (meeting is null)
            return NotFound(new { error = "Meeting not found for organization." });

        var participant = meeting.Participants.FirstOrDefault(x => x.UserId == request.ParticipantUserId);
        if (participant is null)
            return BadRequest(new { error = "Participant user is not part of the meeting." });

        var trackSid = string.IsNullOrWhiteSpace(request.TrackSid)
            ? $"qa-{DateTime.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}"[..35]
            : Slugify(request.TrackSid!);

        var duplicateTrackSid = await _dbContext.ParticipantAudioFragments
            .IgnoreQueryFilters()
            .AnyAsync(x => x.MeetingId == meetingId && x.TrackSid == trackSid, cancellationToken);
        if (duplicateTrackSid)
            return Conflict(new { error = "TrackSid already exists for this meeting.", trackSid });

        var extension = Path.GetExtension(request.File.FileName);
        if (string.IsNullOrWhiteSpace(extension))
            extension = ".wav";

        var objectKey = string.IsNullOrWhiteSpace(request.StorageObjectKey)
            ? $"qa/mtg:{meetingId}/user:{request.ParticipantUserId}/track-{trackSid}{extension.ToLowerInvariant()}"
            : request.StorageObjectKey!.Trim();

        var tempFilePath = Path.Combine(Path.GetTempPath(), $"qa-audio-{Guid.NewGuid():N}{extension}");
        try
        {
            await using (var tempFile = System.IO.File.Create(tempFilePath))
            await using (var uploadStream = request.File.OpenReadStream())
            {
                await uploadStream.CopyToAsync(tempFile, cancellationToken);
            }

            var upload = await _storageService.UploadFileAsync(tempFilePath, objectKey, cancellationToken);
            var now = DateTime.UtcNow;

            var track = await _dbContext.ParticipantAudioTracks
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(
                    x => x.MeetingId == meetingId
                         && x.OrganizationId == request.OrganizationId
                         && x.ParticipantUserId == request.ParticipantUserId,
                    cancellationToken);

            if (track is null)
            {
                track = new ParticipantAudioTrack
                {
                    MeetingId = meetingId,
                    OrganizationId = request.OrganizationId,
                    ParticipantUserId = request.ParticipantUserId
                };
                _dbContext.ParticipantAudioTracks.Add(track);
            }

            track.Status = ParticipantAudioTrackStatus.Available;
            track.StorageObjectKey = objectKey;
            track.SizeBytes = upload.SizeBytes;

            var fragment = new ParticipantAudioFragment
            {
                MeetingId = meetingId,
                OrganizationId = request.OrganizationId,
                ParticipantUserId = request.ParticipantUserId,
                ParticipantAudioTrack = track,
                TrackSid = trackSid,
                StorageObjectKey = objectKey,
                StorageLocation = upload.StorageLocation,
                Status = ParticipantAudioFragmentStatus.Available,
                SizeBytes = upload.SizeBytes,
                DurationSeconds = request.DurationSeconds,
                TrackPublishedAtUtc = request.TrackPublishedAtUtc ?? meeting.RoomActivatedAtUtc ?? now,
                EgressStartedAtUtc = request.EgressStartedAtUtc ?? request.TrackPublishedAtUtc ?? meeting.RoomActivatedAtUtc ?? now,
                EgressEndedAtUtc = request.EgressEndedAtUtc,
                StorageAvailableAtUtc = now
            };

            _dbContext.ParticipantAudioFragments.Add(fragment);
            await _dbContext.SaveChangesAsync(cancellationToken);

            return Ok(new QaAudioFragmentResponse(
                fragment.Id,
                track.Id,
                meeting.Id,
                request.OrganizationId,
                request.ParticipantUserId,
                fragment.TrackSid,
                objectKey,
                upload.StorageLocation,
                upload.SizeBytes,
                fragment.Status.ToString()));
        }
        finally
        {
            try
            {
                if (System.IO.File.Exists(tempFilePath))
                    System.IO.File.Delete(tempFilePath);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to delete QA temp audio file {TempFilePath}", tempFilePath);
            }
        }
    }

    [HttpPost("meetings/{meetingId:guid}/process")]
    [ProducesResponseType(typeof(QaProcessResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> ProcessMeeting(
        [FromRoute] Guid meetingId,
        [FromBody] QaProcessRequest request,
        CancellationToken cancellationToken)
    {
        if (!IsQaHarnessEnabled())
            return NotFound();

        var meetingExists = await _dbContext.Meetings
            .IgnoreQueryFilters()
            .AnyAsync(x => x.Id == meetingId && x.OrganizationId == request.OrganizationId, cancellationToken);
        if (!meetingExists)
            return NotFound(new { error = "Meeting not found for organization." });

        var requestedJobs = NormalizeJobs(request.Jobs);
        var startedAtUtc = DateTime.UtcNow;
        var jobResults = new List<QaProcessJobResponse>();

        if (string.Equals(request.Mode, "enqueue", StringComparison.OrdinalIgnoreCase)
            || string.Equals(request.Mode, "enqueue-and-wait", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var job in requestedJobs)
            {
                var hangfireJobId = await EnqueueJobAsync(job, meetingId, request.OrganizationId, cancellationToken);
                jobResults.Add(new QaProcessJobResponse(job, "enqueued", hangfireJobId, null, null));
            }

            if (string.Equals(request.Mode, "enqueue-and-wait", StringComparison.OrdinalIgnoreCase))
            {
                await WaitForRequestedJobsAsync(
                    meetingId,
                    request.OrganizationId,
                    requestedJobs,
                    TimeSpan.FromSeconds(Math.Clamp(request.TimeoutSeconds ?? 180, 1, 600)),
                    cancellationToken);
            }
        }
        else
        {
            foreach (var job in requestedJobs)
            {
                var jobStartedAtUtc = DateTime.UtcNow;
                try
                {
                    await RunJobInlineAsync(job, meetingId, request.OrganizationId, cancellationToken);
                    jobResults.Add(new QaProcessJobResponse(job, "completed", null, jobStartedAtUtc, DateTime.UtcNow));
                }
                catch (Exception ex)
                {
                    jobResults.Add(new QaProcessJobResponse(job, "failed", null, jobStartedAtUtc, DateTime.UtcNow));
                    _logger.LogError(
                        ex,
                        "QA inline processing job failed. MeetingId={MeetingId} OrganizationId={OrganizationId} Job={Job}",
                        meetingId,
                        request.OrganizationId,
                        job);
                    throw;
                }
            }
        }

        var status = await BuildProcessingStatusAsync(request.OrganizationId, meetingId, cancellationToken);
        return Ok(new QaProcessResponse(request.Mode ?? "inline", startedAtUtc, DateTime.UtcNow, jobResults, status));
    }

    [HttpPost("meetings/{meetingId:guid}/stt-failures")]
    [ProducesResponseType(typeof(QaSttFailureStateResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> ConfigureSttFailures(
        [FromRoute] Guid meetingId,
        [FromBody] QaConfigureSttFailuresRequest? request,
        CancellationToken cancellationToken)
    {
        if (!IsQaHarnessEnabled())
            return NotFound();

        if (request is null)
            return BadRequest(new { error = "Request body is required." });

        if (request.OrganizationId == Guid.Empty)
            return BadRequest(new { error = "OrganizationId is required." });

        if (request.Rules is null || request.Rules.Count == 0)
            return BadRequest(new { error = "At least one STT failure rule is required." });

        var meetingExists = await _dbContext.Meetings
            .IgnoreQueryFilters()
            .AnyAsync(x => x.Id == meetingId && x.OrganizationId == request.OrganizationId, cancellationToken);
        if (!meetingExists)
            return NotFound(new { error = "Meeting not found for organization." });

        foreach (var rule in request.Rules)
        {
            if (string.IsNullOrWhiteSpace(rule.StorageObjectKey))
                return BadRequest(new { error = "Each rule requires storageObjectKey." });

            if (rule.FailCount is < 1)
                return BadRequest(new { error = "Each rule failCount must be at least 1." });
        }

        _qaSttFailureInjectionService.SetRules(meetingId, request.OrganizationId, request.Rules);
        return Ok(_qaSttFailureInjectionService.GetState(meetingId, request.OrganizationId));
    }

    [HttpGet("meetings/{meetingId:guid}/stt-failures")]
    [ProducesResponseType(typeof(QaSttFailureStateResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetSttFailures(
        [FromRoute] Guid meetingId,
        [FromQuery] Guid organizationId,
        CancellationToken cancellationToken)
    {
        if (!IsQaHarnessEnabled())
            return NotFound();

        if (organizationId == Guid.Empty)
            return BadRequest(new { error = "organizationId is required." });

        var meetingExists = await _dbContext.Meetings
            .IgnoreQueryFilters()
            .AnyAsync(x => x.Id == meetingId && x.OrganizationId == organizationId, cancellationToken);
        if (!meetingExists)
            return NotFound(new { error = "Meeting not found for organization." });

        return Ok(_qaSttFailureInjectionService.GetState(meetingId, organizationId));
    }

    [HttpGet("meetings/{meetingId:guid}/processing-status")]
    [ProducesResponseType(typeof(QaProcessingStatusResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetProcessingStatus(
        [FromRoute] Guid meetingId,
        [FromQuery] Guid organizationId,
        CancellationToken cancellationToken)
    {
        if (!IsQaHarnessEnabled())
            return NotFound();

        var meetingExists = await _dbContext.Meetings
            .IgnoreQueryFilters()
            .AnyAsync(x => x.Id == meetingId && x.OrganizationId == organizationId, cancellationToken);
        if (!meetingExists)
            return NotFound(new { error = "Meeting not found for organization." });

        return Ok(await BuildProcessingStatusAsync(organizationId, meetingId, cancellationToken));
    }

    [HttpPost("meetings/{meetingId:guid}/post-processing/stale-tracker-state")]
    [ProducesResponseType(typeof(QaStalePostProcessingTrackerStateResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> SeedStalePostProcessingTrackerState(
        [FromRoute] Guid meetingId,
        [FromBody] QaStalePostProcessingTrackerStateRequest? request,
        CancellationToken cancellationToken)
    {
        if (!IsQaHarnessEnabled())
            return NotFound();

        if (request is null)
            return BadRequest(new { error = "Request body is required." });

        if (request.OrganizationId == Guid.Empty)
            return BadRequest(new { error = "OrganizationId is required." });

        var meetingExists = await _dbContext.Meetings
            .IgnoreQueryFilters()
            .AnyAsync(x => x.Id == meetingId && x.OrganizationId == request.OrganizationId, cancellationToken);
        if (!meetingExists)
            return NotFound(new { error = "Meeting not found for organization." });

        if (!TryParsePostProcessingStepType(request.StepType, out var stepType))
            return BadRequest(new { error = $"Unsupported stepType '{request.StepType}'." });

        if (!TryParsePostProcessingStatus(request.Status, out var stepStatus))
            return BadRequest(new { error = $"Unsupported status '{request.Status}' for stale tracker seeding." });

        if (stepStatus != PostMeetingProcessingStatus.Completed)
            return BadRequest(new { error = "Only Completed step status is supported for stale tracker seeding." });

        var fragments = await _dbContext.ParticipantAudioFragments
            .IgnoreQueryFilters()
            .Where(x => x.MeetingId == meetingId
                        && x.OrganizationId == request.OrganizationId
                        && x.Status == ParticipantAudioFragmentStatus.Available
                        && x.StorageObjectKey != null)
            .OrderBy(x => x.CreatedAtUtc)
            .ThenBy(x => x.Id)
            .ToListAsync(cancellationToken);

        if (fragments.Count == 0)
        {
            return BadRequest(new
            {
                error = "No available uploaded audio fragments exist for this meeting. Upload audio before seeding stale tracker state."
            });
        }

        var oldHangfireJobId = string.IsNullOrWhiteSpace(request.OldHangfireJobId)
            ? "qa-old-stt-job"
            : request.OldHangfireJobId.Trim();
        var fragmentIds = fragments.Select(x => x.Id).ToList();
        var artifact = new PostMeetingArtifactLink("participant_audio_fragment", ArtifactIds: fragmentIds);

        var tracker = _serviceProvider.GetRequiredService<IPostMeetingProcessingTracker>();
        var run = await tracker.EnsureRunAsync(
            request.OrganizationId,
            meetingId,
            relatedHangfireJobId: oldHangfireJobId,
            cancellationToken: cancellationToken);

        var step = await tracker.CompleteStepAsync(
            request.OrganizationId,
            meetingId,
            stepType,
            relatedHangfireJobId: oldHangfireJobId,
            message: "QA stale post-processing tracker state seeded.",
            artifact: artifact,
            cancellationToken: cancellationToken);

        if (request.CompleteRun)
        {
            run = await tracker.CompleteRunAsync(
                request.OrganizationId,
                meetingId,
                relatedHangfireJobId: oldHangfireJobId,
                message: "QA stale post-processing run completion seeded.",
                cancellationToken: cancellationToken);
        }

        return Ok(new QaStalePostProcessingTrackerStateResponse(
            request.OrganizationId,
            meetingId,
            run.Id,
            run.PipelineGenerationId,
            step.Id,
            oldHangfireJobId,
            step.StepType.ToString(),
            step.Status.ToString(),
            run.Status.ToString(),
            fragmentIds));
    }

    [HttpPost("meetings/{meetingId:guid}/rag/fixture-artifacts")]
    [ProducesResponseType(typeof(QaRagFixtureArtifactsResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> SeedRagFixtureArtifacts(
        [FromRoute] Guid meetingId,
        [FromBody] QaRagFixtureArtifactsRequest? request,
        CancellationToken cancellationToken)
    {
        if (!IsQaHarnessEnabled())
            return NotFound();

        if (request is null)
            return BadRequest(new { error = "Request body is required." });

        if (request.OrganizationId == Guid.Empty)
            return BadRequest(new { error = "OrganizationId is required." });

        if (string.IsNullOrWhiteSpace(request.TranscriptText))
            return BadRequest(new { error = "TranscriptText is required." });

        if (string.IsNullOrWhiteSpace(request.SummaryText))
            return BadRequest(new { error = "SummaryText is required." });

        var meeting = await _dbContext.Meetings
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(
                x => x.Id == meetingId && x.OrganizationId == request.OrganizationId,
                cancellationToken);
        if (meeting is null)
            return NotFound(new { error = "Meeting not found for organization." });

        if (meeting.Status != MeetingStatus.Completed)
        {
            meeting.Status = MeetingStatus.Completed;
            meeting.UpdatedAtUtc = DateTime.UtcNow;
        }

        var now = DateTime.UtcNow;
        var transcript = await _dbContext.MeetingTranscripts
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(
                x => x.MeetingId == meetingId && x.OrganizationId == request.OrganizationId,
                cancellationToken);

        if (transcript is null)
        {
            transcript = new MeetingTranscript
            {
                Id = Guid.NewGuid(),
                OrganizationId = request.OrganizationId,
                MeetingId = meetingId
            };
            _dbContext.MeetingTranscripts.Add(transcript);
        }

        transcript.FullText = request.TranscriptText.Trim();
        transcript.SegmentsJson = "[]";
        transcript.SttModel = "qa-fixture";
        transcript.GeneratedAtUtc = now;
        transcript.CompletenessStatus = MeetingTranscriptCompletenessStatus.Complete;
        transcript.ExpectedAudioFragmentCount = 0;
        transcript.TranscribedAudioFragmentCount = 0;
        transcript.RetryableFailedAudioFragmentCount = 0;
        transcript.TerminalFailedAudioFragmentCount = 0;
        transcript.MissingAudioFragmentIdsJson = "[]";
        transcript.WarningsJson = "[]";
        transcript.UpdatedAtUtc = now;

        var summary = await _dbContext.MeetingSummaries
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(
                x => x.MeetingId == meetingId && x.OrganizationId == request.OrganizationId,
                cancellationToken);

        if (summary is null)
        {
            summary = new MeetingSummary
            {
                Id = Guid.NewGuid(),
                OrganizationId = request.OrganizationId,
                MeetingId = meetingId
            };
            _dbContext.MeetingSummaries.Add(summary);
        }

        summary.SummaryText = request.SummaryText.Trim();
        summary.LlmModel = "qa-fixture";
        summary.GeneratedAtUtc = now;
        summary.UpdatedAtUtc = now;

        var actionItemCount = 0;
        if (request.ActionItems is { Count: > 0 })
        {
            var hostUserId = await _dbContext.MeetingParticipants
                .IgnoreQueryFilters()
                .Where(x => x.MeetingId == meetingId && x.OrganizationId == request.OrganizationId)
                .OrderByDescending(x => x.MeetingRole == MeetingRole.Host)
                .ThenBy(x => x.MeetingRole)
                .Select(x => x.UserId)
                .FirstOrDefaultAsync(cancellationToken);

            foreach (var title in request.ActionItems)
            {
                if (string.IsNullOrWhiteSpace(title))
                    continue;

                _dbContext.ActionItems.Add(new ActionItem
                {
                    Id = Guid.NewGuid(),
                    OrganizationId = request.OrganizationId,
                    MeetingId = meetingId,
                    Title = TruncateForStorage(title, 500),
                    AssignedToUserId = hostUserId == Guid.Empty ? null : hostUserId,
                    Status = ActionItemStatus.PendingReview,
                    ExtractedAtUtc = now
                });
                actionItemCount++;
            }
        }

        await _dbContext.SaveChangesAsync(cancellationToken);

        return Ok(new QaRagFixtureArtifactsResponse(
            request.OrganizationId,
            meetingId,
            meeting.Status.ToString(),
            transcript.Id,
            summary.Id,
            actionItemCount));
    }

    [HttpPost("meetings/{meetingId:guid}/source-revision/fixture")]
    [ProducesResponseType(typeof(QaSourceRevisionFixtureResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> SeedSourceRevisionFixture(
        [FromRoute] Guid meetingId,
        [FromBody] QaSourceRevisionFixtureRequest? request,
        CancellationToken cancellationToken)
    {
        if (!IsQaHarnessEnabled())
            return NotFound();

        if (request is null)
            return BadRequest(new { error = "Request body is required." });

        if (request.OrganizationId == Guid.Empty)
            return BadRequest(new { error = "OrganizationId is required." });

        if (string.IsNullOrWhiteSpace(request.SourceLabel))
            return BadRequest(new { error = "SourceLabel is required." });

        if (string.IsNullOrWhiteSpace(request.TranscriptText))
            return BadRequest(new { error = "TranscriptText is required." });

        if (string.IsNullOrWhiteSpace(request.SummaryText))
            return BadRequest(new { error = "SummaryText is required." });

        var meeting = await _dbContext.Meetings
            .IgnoreQueryFilters()
            .Include(x => x.Participants)
            .FirstOrDefaultAsync(
                x => x.Id == meetingId && x.OrganizationId == request.OrganizationId,
                cancellationToken);
        if (meeting is null)
            return NotFound(new { error = "Meeting not found for organization." });

        if (meeting.Status != MeetingStatus.Completed)
        {
            meeting.Status = MeetingStatus.Completed;
            meeting.UpdatedAtUtc = DateTime.UtcNow;
        }

        var sourceLabel = request.SourceLabel.Trim();
        var qaSourceTag = $"qa-source-{sourceLabel}";
        var now = DateTime.UtcNow;

        var transcript = await _dbContext.MeetingTranscripts
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(
                x => x.MeetingId == meetingId && x.OrganizationId == request.OrganizationId,
                cancellationToken);

        if (transcript is null)
        {
            transcript = new MeetingTranscript
            {
                Id = Guid.NewGuid(),
                OrganizationId = request.OrganizationId,
                MeetingId = meetingId
            };
            _dbContext.MeetingTranscripts.Add(transcript);
        }

        transcript.FullText = request.TranscriptText.Trim();
        transcript.SegmentsJson = "[]";
        transcript.SttModel = qaSourceTag;
        transcript.GeneratedAtUtc = now;
        transcript.CompletenessStatus = MeetingTranscriptCompletenessStatus.Complete;
        transcript.ExpectedAudioFragmentCount = 0;
        transcript.TranscribedAudioFragmentCount = 0;
        transcript.RetryableFailedAudioFragmentCount = 0;
        transcript.TerminalFailedAudioFragmentCount = 0;
        transcript.MissingAudioFragmentIdsJson = "[]";
        transcript.WarningsJson = "[]";
        transcript.UpdatedAtUtc = now;
        TranscriptSourceIdentity.InitializeNew(transcript, transcript.FullText);

        var summary = await _dbContext.MeetingSummaries
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(
                x => x.MeetingId == meetingId && x.OrganizationId == request.OrganizationId,
                cancellationToken);

        if (summary is null)
        {
            summary = new MeetingSummary
            {
                Id = Guid.NewGuid(),
                OrganizationId = request.OrganizationId,
                MeetingId = meetingId
            };
            _dbContext.MeetingSummaries.Add(summary);
        }

        summary.SummaryText = request.SummaryText.Trim();
        summary.LlmModel = qaSourceTag;
        summary.GeneratedAtUtc = now;
        summary.UpdatedAtUtc = now;
        var transcriptIdentity = TranscriptSourceIdentity.From(transcript);
        TranscriptSourceIdentity.ApplySourceFields(summary, transcriptIdentity);

        var hostUserId = meeting.Participants
            .Where(participant => participant.MeetingRole == MeetingRole.Host)
            .Select(participant => participant.UserId)
            .FirstOrDefault();
        if (hostUserId == Guid.Empty)
        {
            hostUserId = meeting.Participants.Select(participant => participant.UserId).FirstOrDefault();
        }

        var actionItemIds = new List<Guid>();
        if (request.ActionItems is { Count: > 0 })
        {
            foreach (var actionItemRequest in request.ActionItems)
            {
                if (string.IsNullOrWhiteSpace(actionItemRequest.Title))
                    continue;

                var actionItemId = Guid.NewGuid();
                var actionItem = new ActionItem
                {
                    Id = actionItemId,
                    OrganizationId = request.OrganizationId,
                    MeetingId = meetingId,
                    Title = TruncateForStorage(actionItemRequest.Title, 500),
                    Description = string.IsNullOrWhiteSpace(actionItemRequest.Description)
                        ? null
                        : TruncateForStorage(actionItemRequest.Description, 2000),
                    AssignedToUserId = actionItemRequest.AssignedToUserId == Guid.Empty
                        ? hostUserId == Guid.Empty ? null : hostUserId
                        : actionItemRequest.AssignedToUserId,
                    Status = ActionItemStatus.PendingReview,
                    ExtractedAtUtc = now
                };
                TranscriptSourceIdentity.ApplySourceFields(actionItem, transcriptIdentity);
                _dbContext.ActionItems.Add(actionItem);
                actionItemIds.Add(actionItemId);
            }
        }

        var personalizedSummaryIds = new List<Guid>();
        if (request.PersonalizedSummaries is { Count: > 0 })
        {
            foreach (var personalizedRequest in request.PersonalizedSummaries)
            {
                if (personalizedRequest.UserId == Guid.Empty || string.IsNullOrWhiteSpace(personalizedRequest.SummaryText))
                    continue;

                var participant = meeting.Participants.FirstOrDefault(x => x.UserId == personalizedRequest.UserId);
                if (participant is null)
                    continue;

                var targetDisplayName = await _dbContext.Users
                    .IgnoreQueryFilters()
                    .Where(user => user.Id == personalizedRequest.UserId)
                    .Select(user => user.DisplayName ?? user.Email ?? user.UserName)
                    .FirstOrDefaultAsync(cancellationToken);

                var existing = await _dbContext.PersonalizedMeetingSummaries
                    .IgnoreQueryFilters()
                    .FirstOrDefaultAsync(
                        x => x.MeetingId == meetingId
                             && x.OrganizationId == request.OrganizationId
                             && x.UserId == personalizedRequest.UserId,
                        cancellationToken);

                var eligibilityReason = string.IsNullOrWhiteSpace(personalizedRequest.EligibilityReason)
                    ? "qa_source_fixture"
                    : personalizedRequest.EligibilityReason.Trim();
                var contextJson = JsonSerializer.Serialize(
                    new { sourceLabel, fixture = "qa-source-revision" },
                    JsonOptions);

                if (existing is null)
                {
                    existing = new PersonalizedMeetingSummary
                    {
                        Id = Guid.NewGuid(),
                        OrganizationId = request.OrganizationId,
                        MeetingId = meetingId,
                        MeetingParticipantId = participant.Id,
                        UserId = personalizedRequest.UserId
                    };
                    _dbContext.PersonalizedMeetingSummaries.Add(existing);
                }

                existing.MeetingParticipantId = participant.Id;
                existing.Status = PersonalizedMeetingSummaryStatus.Generated;
                existing.SummaryText = personalizedRequest.SummaryText.Trim();
                existing.LlmModel = qaSourceTag;
                existing.GeneratedAtUtc = now;
                existing.TargetDisplayName = targetDisplayName;
                existing.EligibilityReason = eligibilityReason;
                existing.EligibilityContextJson = contextJson;
                existing.PersonalizationContextJson = contextJson;
                existing.UpdatedAtUtc = now;
                TranscriptSourceIdentity.ApplySourceFields(existing, transcriptIdentity);
                personalizedSummaryIds.Add(existing.Id);
            }
        }

        await _dbContext.SaveChangesAsync(cancellationToken);

        Guid? pipelineGenerationId = null;
        Guid? postProcessingRunId = null;
        if (request.MarkPostProcessingCompleted)
        {
            var tracker = _serviceProvider.GetRequiredService<IPostMeetingProcessingTracker>();
            var hangfireJobId = $"{qaSourceTag}-seed";
            pipelineGenerationId = Guid.NewGuid();
            var run = await tracker.EnsureRunAsync(
                request.OrganizationId,
                meetingId,
                pipelineGenerationId,
                relatedHangfireJobId: hangfireJobId,
                cancellationToken: cancellationToken);
            postProcessingRunId = run.Id;

            await tracker.CompleteStepAsync(
                request.OrganizationId,
                meetingId,
                PostMeetingProcessingStepType.SummaryGeneration,
                pipelineGenerationId,
                relatedHangfireJobId: hangfireJobId,
                message: "QA source-revision fixture seeded summary generation.",
                artifact: new PostMeetingArtifactLink("meeting_summary", summary.Id),
                cancellationToken: cancellationToken);

            await tracker.CompleteStepAsync(
                request.OrganizationId,
                meetingId,
                PostMeetingProcessingStepType.ActionExtraction,
                pipelineGenerationId,
                relatedHangfireJobId: hangfireJobId,
                message: "QA source-revision fixture seeded action extraction.",
                artifact: actionItemIds.Count > 0
                    ? new PostMeetingArtifactLink("action_item", ArtifactIds: actionItemIds)
                    : null,
                cancellationToken: cancellationToken);

            await tracker.CompleteStepAsync(
                request.OrganizationId,
                meetingId,
                PostMeetingProcessingStepType.PersonalizedSummaryGeneration,
                pipelineGenerationId,
                relatedHangfireJobId: hangfireJobId,
                message: "QA source-revision fixture seeded personalized summary generation.",
                artifact: personalizedSummaryIds.Count > 0
                    ? new PostMeetingArtifactLink("personalized_meeting_summary", ArtifactIds: personalizedSummaryIds)
                    : null,
                cancellationToken: cancellationToken);

            await tracker.CompleteStepAsync(
                request.OrganizationId,
                meetingId,
                PostMeetingProcessingStepType.KnowledgeIndexing,
                pipelineGenerationId,
                relatedHangfireJobId: hangfireJobId,
                message: "QA source-revision fixture seeded knowledge indexing.",
                cancellationToken: cancellationToken);

            await tracker.CompleteRunAsync(
                request.OrganizationId,
                meetingId,
                pipelineGenerationId,
                relatedHangfireJobId: hangfireJobId,
                message: "QA source-revision fixture completed post-processing run.",
                cancellationToken: cancellationToken);
        }

        return Ok(new QaSourceRevisionFixtureResponse(
            request.OrganizationId,
            meetingId,
            sourceLabel,
            transcript.Id,
            summary.Id,
            actionItemIds,
            personalizedSummaryIds,
            pipelineGenerationId,
            postProcessingRunId));
    }

    [HttpPost("meetings/{meetingId:guid}/source-revision/transcript")]
    [ProducesResponseType(typeof(QaSourceRevisionTranscriptResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> ReplaceSourceRevisionTranscript(
        [FromRoute] Guid meetingId,
        [FromBody] QaSourceRevisionTranscriptRequest? request,
        CancellationToken cancellationToken)
    {
        if (!IsQaHarnessEnabled())
            return NotFound();

        if (request is null)
            return BadRequest(new { error = "Request body is required." });

        if (request.OrganizationId == Guid.Empty)
            return BadRequest(new { error = "OrganizationId is required." });

        if (string.IsNullOrWhiteSpace(request.SourceLabel))
            return BadRequest(new { error = "SourceLabel is required." });

        if (string.IsNullOrWhiteSpace(request.TranscriptText))
            return BadRequest(new { error = "TranscriptText is required." });

        var meetingExists = await _dbContext.Meetings
            .IgnoreQueryFilters()
            .AnyAsync(x => x.Id == meetingId && x.OrganizationId == request.OrganizationId, cancellationToken);
        if (!meetingExists)
            return NotFound(new { error = "Meeting not found for organization." });

        var sourceLabel = request.SourceLabel.Trim();
        var qaSourceTag = $"qa-source-{sourceLabel}";
        var now = DateTime.UtcNow;

        var transcript = await _dbContext.MeetingTranscripts
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(
                x => x.MeetingId == meetingId && x.OrganizationId == request.OrganizationId,
                cancellationToken);

        var previousFullText = transcript?.FullText;
        var previousFullTextSha256 = string.IsNullOrWhiteSpace(previousFullText)
            ? null
            : Sha256(previousFullText);

        if (transcript is null)
        {
            transcript = new MeetingTranscript
            {
                Id = Guid.NewGuid(),
                OrganizationId = request.OrganizationId,
                MeetingId = meetingId
            };
            _dbContext.MeetingTranscripts.Add(transcript);
        }

        transcript.FullText = request.TranscriptText.Trim();
        transcript.SegmentsJson = "[]";
        transcript.SttModel = qaSourceTag;
        transcript.GeneratedAtUtc = now;
        transcript.CompletenessStatus = MeetingTranscriptCompletenessStatus.Complete;
        transcript.ExpectedAudioFragmentCount = 0;
        transcript.TranscribedAudioFragmentCount = 0;
        transcript.RetryableFailedAudioFragmentCount = 0;
        transcript.TerminalFailedAudioFragmentCount = 0;
        transcript.MissingAudioFragmentIdsJson = "[]";
        transcript.WarningsJson = "[]";
        transcript.UpdatedAtUtc = now;
        TranscriptSourceIdentity.ApplyContentRevision(transcript, transcript.FullText);

        await _dbContext.SaveChangesAsync(cancellationToken);

        var currentFullTextSha256 = Sha256(transcript.FullText);
        var transcriptChanged = !string.Equals(previousFullTextSha256, currentFullTextSha256, StringComparison.Ordinal);

        Guid? pipelineGenerationId = null;
        Guid? postProcessingRunId = null;
        if (request.BeginPipelineGeneration)
        {
            pipelineGenerationId = Guid.NewGuid();
            var tracker = _serviceProvider.GetRequiredService<IPostMeetingProcessingTracker>();
            var run = await tracker.EnsureRunAsync(
                request.OrganizationId,
                meetingId,
                pipelineGenerationId,
                relatedHangfireJobId: $"{qaSourceTag}-transcript",
                cancellationToken: cancellationToken);
            postProcessingRunId = run.Id;
        }

        return Ok(new QaSourceRevisionTranscriptResponse(
            request.OrganizationId,
            meetingId,
            sourceLabel,
            transcript.Id,
            previousFullTextSha256,
            currentFullTextSha256,
            transcript.TranscriptHash,
            transcript.TranscriptRevision,
            transcriptChanged,
            pipelineGenerationId,
            postProcessingRunId));
    }

    [HttpPost("meetings/{meetingId:guid}/rag/duplicate-current")]
    [ProducesResponseType(typeof(QaRagDuplicateCurrentResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> SeedRagDuplicateCurrent(
        [FromRoute] Guid meetingId,
        [FromBody] QaRagDuplicateCurrentRequest? request,
        CancellationToken cancellationToken)
    {
        if (!IsQaHarnessEnabled())
            return NotFound();

        if (request is null)
            return BadRequest(new { error = "Request body is required." });

        if (request.OrganizationId == Guid.Empty)
            return BadRequest(new { error = "OrganizationId is required." });

        if (request.ArtifactVersion is < 1)
            return BadRequest(new { error = "ArtifactVersion must be at least 1." });

        if (!TryParseKnowledgeArtifactType(request.ArtifactType, out var artifactType))
            return BadRequest(new { error = "ArtifactType is invalid.", allowedValues = Enum.GetNames<KnowledgeArtifactType>() });

        var meetingExists = await _dbContext.Meetings
            .IgnoreQueryFilters()
            .AnyAsync(x => x.Id == meetingId && x.OrganizationId == request.OrganizationId, cancellationToken);
        if (!meetingExists)
            return NotFound(new { error = "Meeting not found for organization." });

        var sourceDocument = await _dbContext.KnowledgeDocuments
            .IgnoreQueryFilters()
            .Where(x => x.OrganizationId == request.OrganizationId
                        && x.MeetingId == meetingId
                        && x.ArtifactType == artifactType
                        && x.ArtifactVersion == request.ArtifactVersion
                        && x.Visibility == KnowledgeVisibility.Published
                        && x.IsCurrent)
            .OrderBy(x => x.GeneratedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);

        if (sourceDocument is null)
        {
            return NotFound(new
            {
                error = "No current published knowledge document exists for the requested artifact key.",
                artifactType = artifactType.ToString(),
                artifactVersion = request.ArtifactVersion
            });
        }

        await DropKnowledgeDocumentCurrentPublishedArtifactIndexAsync(cancellationToken);

        var duplicateGenerationId = Guid.NewGuid();
        var duplicateDocumentId = Guid.NewGuid();
        var generatedAtUtc = DateTime.UtcNow;

        var duplicateDocument = new KnowledgeDocument
        {
            Id = duplicateDocumentId,
            OrganizationId = sourceDocument.OrganizationId,
            MeetingId = sourceDocument.MeetingId,
            ArtifactType = sourceDocument.ArtifactType,
            ArtifactId = sourceDocument.ArtifactId,
            ArtifactVersion = sourceDocument.ArtifactVersion,
            Title = sourceDocument.Title,
            ContentHash = sourceDocument.ContentHash,
            IndexGenerationId = duplicateGenerationId,
            Visibility = KnowledgeVisibility.Published,
            IsCurrent = true,
            EmbeddingProvider = sourceDocument.EmbeddingProvider,
            EmbeddingModel = sourceDocument.EmbeddingModel,
            EmbeddingDimension = sourceDocument.EmbeddingDimension,
            MetadataJson = sourceDocument.MetadataJson,
            GeneratedAtUtc = generatedAtUtc
        };

        _dbContext.KnowledgeDocuments.Add(duplicateDocument);
        await _dbContext.SaveChangesAsync(cancellationToken);

        var duplicateCurrentCount = await _dbContext.KnowledgeDocuments
            .IgnoreQueryFilters()
            .CountAsync(
                x => x.OrganizationId == request.OrganizationId
                     && x.MeetingId == meetingId
                     && x.ArtifactType == artifactType
                     && x.ArtifactId == sourceDocument.ArtifactId
                     && x.ArtifactVersion == request.ArtifactVersion
                     && x.Visibility == KnowledgeVisibility.Published
                     && x.IsCurrent,
                cancellationToken);

        return Ok(new QaRagDuplicateCurrentResponse(
            request.OrganizationId,
            meetingId,
            sourceDocument.Id,
            duplicateDocumentId,
            artifactType.ToString(),
            sourceDocument.ArtifactId,
            sourceDocument.ArtifactVersion,
            duplicateCurrentCount));
    }

    private async Task DropKnowledgeDocumentCurrentPublishedArtifactIndexAsync(CancellationToken cancellationToken)
    {
        await _dbContext.Database.ExecuteSqlRawAsync(
            """
            DROP INDEX IF EXISTS "UX_KnowledgeDocuments_CurrentPublishedArtifact";
            """,
            cancellationToken);
    }

    [HttpPost("meetings/{meetingId:guid}/transcript-preview")]
    [ProducesResponseType(typeof(QaMeetingTranscriptPreviewResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> PreviewMeetingTranscript(
        [FromRoute] Guid meetingId,
        CancellationToken cancellationToken)
    {
        if (!IsQaHarnessEnabled())
            return NotFound();

        if (meetingId == Guid.Empty)
            return BadRequest(new { error = "MeetingId is required." });

        var preview = await _meetingTranscriptPreviewService.PreviewAsync(meetingId, cancellationToken);
        if (preview is null)
        {
            var meetingExists = await _dbContext.Meetings
                .IgnoreQueryFilters()
                .AnyAsync(x => x.Id == meetingId, cancellationToken);

            return meetingExists
                ? BadRequest(new { error = "No transcript preview was produced for this meeting." })
                : NotFound(new { error = "Meeting not found." });
        }

        return Ok(new QaMeetingTranscriptPreviewResponse(
            preview.OrganizationId,
            preview.MeetingId,
            preview.FullText,
            preview.SegmentsJson,
            preview.SttModel,
            preview.CompletenessStatus.ToString(),
            preview.Warnings,
            preview.ExpectedAudioFragmentCount,
            preview.TranscribedAudioFragmentCount,
            preview.RetryableFailedAudioFragmentCount,
            preview.TerminalFailedAudioFragmentCount,
            preview.GeneratedAtUtc,
            preview.ExistingTranscriptId,
            preview.ExistingTranscriptHash,
            preview.ExistingTranscriptRevision,
            preview.PreviewTranscriptHash,
            preview.PreviewTranscriptRevision,
            preview.WouldChangeExistingTranscript));
    }

    private bool IsQaHarnessEnabled()
    {
        if (_environment.IsDevelopment() || _environment.IsEnvironment("Testing"))
            return true;

        return !_environment.IsProduction()
               && _configuration.GetValue<bool>("QaHarness:Enabled");
    }

    private async Task<QaProcessingStatusResponse> BuildProcessingStatusAsync(
        Guid organizationId,
        Guid meetingId,
        CancellationToken cancellationToken)
    {
        var meeting = await _dbContext.Meetings
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(x => x.Id == meetingId && x.OrganizationId == organizationId)
            .Select(x => new
            {
                x.Id,
                x.OrganizationId,
                x.Title,
                x.Status,
                x.RoomActivatedAtUtc,
                x.AiAssistantEnabled
            })
            .FirstAsync(cancellationToken);

        var participantRows = await _dbContext.MeetingParticipants
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(x => x.MeetingId == meetingId && x.OrganizationId == organizationId)
            .Select(x => new
            {
                x.Id,
                x.UserId,
                x.MeetingRole,
                x.User.DisplayName,
                x.User.Email,
                x.User.UserName
            })
            .ToListAsync(cancellationToken);

        var participantNames = participantRows
            .Select(x => new QaParticipantName(
                x.Id,
                x.UserId,
                x.MeetingRole,
                x.DisplayName,
                x.Email,
                x.UserName))
            .ToList();

        var participantByUserId = participantNames.ToDictionary(x => x.UserId);

        var tracks = await _dbContext.ParticipantAudioTracks
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(x => x.MeetingId == meetingId && x.OrganizationId == organizationId)
            .OrderBy(x => x.ParticipantUserId)
            .Select(x => new QaAudioTrackStatusResponse(
                x.Id,
                x.ParticipantUserId,
                null,
                x.Status.ToString(),
                x.StorageObjectKey,
                x.SizeBytes,
                x.DurationSeconds))
            .ToListAsync(cancellationToken);

        tracks = tracks
            .Select(track => track with
            {
                ParticipantDisplayName = ResolveDisplayName(participantByUserId, track.ParticipantUserId)
            })
            .ToList();

        var fragments = await _dbContext.ParticipantAudioFragments
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(x => x.MeetingId == meetingId && x.OrganizationId == organizationId)
            .OrderBy(x => x.TrackPublishedAtUtc)
            .ThenBy(x => x.ParticipantUserId)
            .Select(x => new QaAudioFragmentStatusResponse(
                x.Id,
                x.ParticipantAudioTrackId,
                x.ParticipantUserId,
                null,
                x.TrackSid,
                x.Status.ToString(),
                x.SttStatus.ToString(),
                x.StorageObjectKey,
                x.StorageLocation,
                x.SizeBytes,
                x.DurationSeconds,
                x.TrackPublishedAtUtc,
                x.StorageAvailableAtUtc,
                x.FailedAtUtc,
                x.FailureCode,
                x.FailureMessage,
                x.SttAttemptCount,
                x.LastSttAttemptAtUtc,
                x.LastSttSucceededAtUtc,
                x.LastSttFailedAtUtc,
                x.SttFailureCode,
                x.SttFailureMessage,
                x.SttModel,
                x.SttSegmentCount,
                x.SttStatus == ParticipantAudioFragmentSttStatus.FailedRetryable))
            .ToListAsync(cancellationToken);

        fragments = fragments
            .Select(fragment => fragment with
            {
                ParticipantDisplayName = fragment.ParticipantUserId.HasValue
                    ? ResolveDisplayName(participantByUserId, fragment.ParticipantUserId.Value)
                    : fragment.ParticipantDisplayName ?? "AI Assistant"
            })
            .ToList();

        var transcript = await _dbContext.MeetingTranscripts
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.MeetingId == meetingId && x.OrganizationId == organizationId, cancellationToken);

        var summary = await _dbContext.MeetingSummaries
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.MeetingId == meetingId && x.OrganizationId == organizationId, cancellationToken);

        var personalizedRows = await _dbContext.PersonalizedMeetingSummaries
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(x => x.MeetingId == meetingId && x.OrganizationId == organizationId)
            .OrderBy(x => x.UserId)
            .Select(x => new
            {
                x.Id,
                x.UserId,
                DisplayName = x.User.DisplayName ?? x.User.Email ?? x.UserId.ToString(),
                x.Status,
                x.GeneratedAtUtc,
                x.EligibilityReason,
                x.SummaryText,
                x.SourceTranscriptId,
                x.SourceTranscriptHash,
                x.SourceTranscriptRevision,
                x.SourceTranscriptGeneratedAtUtc
            })
            .ToListAsync(cancellationToken);

        var personalized = personalizedRows
            .Select(x => new QaPersonalizedSummaryStatusResponse(
                x.Id,
                x.UserId,
                x.DisplayName,
                x.Status.ToString(),
                x.GeneratedAtUtc,
                x.EligibilityReason,
                x.SummaryText == null ? 0 : x.SummaryText.Length,
                string.IsNullOrWhiteSpace(x.SummaryText) ? null : Sha256(x.SummaryText),
                string.IsNullOrWhiteSpace(x.SummaryText) ? string.Empty : Preview(x.SummaryText),
                x.SourceTranscriptId,
                x.SourceTranscriptHash,
                x.SourceTranscriptRevision,
                x.SourceTranscriptGeneratedAtUtc))
            .ToList();

        var actionItemRows = await _dbContext.ActionItems
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(x => x.MeetingId == meetingId
                        && x.OrganizationId == organizationId
                        && x.SupersededAtUtc == null)
            .OrderBy(x => x.CreatedAtUtc)
            .Select(x => new
            {
                x.Id,
                x.Title,
                x.Description,
                x.Status,
                x.AssignedToUserId,
                x.DueDateUtc,
                x.ExtractedAtUtc,
                x.SourceTranscriptId,
                x.SourceTranscriptHash,
                x.SourceTranscriptRevision,
                x.SourceTranscriptGeneratedAtUtc,
                x.SupersededAtUtc
            })
            .ToListAsync(cancellationToken);

        var actionItems = actionItemRows
            .Select(x =>
            {
                var previewSource = string.IsNullOrWhiteSpace(x.Description)
                    ? x.Title
                    : $"{x.Title} {x.Description}";
                return new QaActionItemStatusResponse(
                    x.Id,
                    x.Title,
                    x.Status.ToString(),
                    x.AssignedToUserId,
                    x.DueDateUtc,
                    x.ExtractedAtUtc,
                    string.IsNullOrWhiteSpace(x.Description) ? null : Sha256(x.Description),
                    Preview(previewSource),
                    x.SourceTranscriptId,
                    x.SourceTranscriptHash,
                    x.SourceTranscriptRevision,
                    x.SourceTranscriptGeneratedAtUtc,
                    x.SupersededAtUtc);
            })
            .ToList();

        var knowledgeDocuments = await _dbContext.KnowledgeDocuments
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(x => x.MeetingId == meetingId && x.OrganizationId == organizationId)
            .OrderBy(x => x.ArtifactType)
            .ThenBy(x => x.ArtifactId)
            .ThenBy(x => x.ArtifactVersion)
            .Select(x => new QaKnowledgeDocumentStatusResponse(
                x.Id,
                x.ArtifactType.ToString(),
                x.ArtifactId,
                x.ArtifactVersion,
                x.IsCurrent,
                x.Visibility.ToString(),
                x.ContentHash,
                x.IndexGenerationId,
                x.GeneratedAtUtc,
                x.SourceTranscriptId,
                x.SourceTranscriptHash,
                x.SourceTranscriptRevision,
                x.SourceTranscriptGeneratedAtUtc))
            .ToListAsync(cancellationToken);

        var postProcessingRuns = await _dbContext.PostMeetingProcessingRuns
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(x => x.MeetingId == meetingId && x.OrganizationId == organizationId)
            .OrderByDescending(x => x.CreatedAtUtc)
            .Select(x => new QaPostProcessingRunStatusResponse(
                x.Id,
                x.Status.ToString(),
                x.StartedAtUtc,
                x.CompletedAtUtc,
                x.FailedAtUtc,
                x.RelatedHangfireJobId,
                x.PipelineGenerationId))
            .ToListAsync(cancellationToken);

        var postProcessingSteps = await _dbContext.PostMeetingProcessingSteps
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(x => x.MeetingId == meetingId && x.OrganizationId == organizationId)
            .OrderBy(x => x.StepType)
            .Select(x => new QaPostProcessingStepStatusResponse(
                x.Id,
                x.StepType.ToString(),
                x.Status.ToString(),
                x.AttemptCount,
                x.StartedAtUtc,
                x.CompletedAtUtc,
                x.FailedAtUtc,
                x.RelatedHangfireJobId,
                x.ErrorCode,
                x.ErrorMessage))
            .ToListAsync(cancellationToken);

        var recentEvents = await _dbContext.PostMeetingProcessingEvents
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(x => x.MeetingId == meetingId && x.OrganizationId == organizationId)
            .OrderByDescending(x => x.CreatedAtUtc)
            .Take(50)
            .OrderBy(x => x.CreatedAtUtc)
            .Select(x => new QaPostProcessingEventStatusResponse(
                x.Id,
                x.StepType == null ? null : x.StepType.ToString(),
                x.EventType.ToString(),
                x.Status == null ? null : x.Status.ToString(),
                x.Message ?? string.Empty,
                x.RelatedHangfireJobId,
                x.ErrorCode,
                x.ErrorMessage,
                x.CreatedAtUtc))
            .ToListAsync(cancellationToken);

        var currentDuplicates = knowledgeDocuments
            .Where(x => x.IsCurrent)
            .GroupBy(x => new { x.ArtifactType, x.ArtifactId, x.ArtifactVersion })
            .Where(group => group.Count() > 1)
            .Select(group => new QaKnowledgeDuplicateResponse(
                group.Key.ArtifactType,
                group.Key.ArtifactId,
                group.Key.ArtifactVersion,
                group.Select(x => x.Id).ToList()))
            .ToList();

        return new QaProcessingStatusResponse(
            new QaMeetingStatusResponse(
                meeting.Id,
                meeting.OrganizationId,
                meeting.Title,
                meeting.Status.ToString(),
                meeting.RoomActivatedAtUtc,
                meeting.AiAssistantEnabled),
            participantNames
                .OrderBy(x => x.DisplayName ?? x.Email ?? x.UserName)
                .Select(x => new QaParticipantStatusResponse(
                    x.Id,
                    x.UserId,
                    x.DisplayName ?? x.Email ?? x.UserName ?? x.UserId.ToString(),
                    x.MeetingRole.ToString()))
                .ToList(),
            tracks,
            fragments,
            transcript is null
                ? null
                : new QaTranscriptStatusResponse(
                    transcript.Id,
                    transcript.GeneratedAtUtc,
                    transcript.SttModel,
                    transcript.FullText.Length,
                    CountSegments(transcript.SegmentsJson),
                    Sha256(transcript.FullText),
                    transcript.TranscriptHash,
                    transcript.TranscriptRevision,
                    Preview(transcript.FullText),
                    transcript.CompletenessStatus.ToString(),
                    transcript.CompletenessStatus != MeetingTranscriptCompletenessStatus.Complete,
                    transcript.ExpectedAudioFragmentCount,
                    transcript.TranscribedAudioFragmentCount,
                    transcript.RetryableFailedAudioFragmentCount,
                    transcript.TerminalFailedAudioFragmentCount,
                    DeserializeStringList(transcript.MissingAudioFragmentIdsJson),
                    DeserializeStringList(transcript.WarningsJson)),
            summary is null
                ? null
                : new QaSummaryStatusResponse(
                    summary.Id,
                    summary.GeneratedAtUtc,
                    summary.LlmModel,
                    summary.SummaryText.Length,
                    Sha256(summary.SummaryText),
                    Preview(summary.SummaryText),
                    summary.SourceTranscriptId,
                    summary.SourceTranscriptHash,
                    summary.SourceTranscriptRevision,
                    summary.SourceTranscriptGeneratedAtUtc),
            personalized,
            actionItems,
            knowledgeDocuments,
            currentDuplicates,
            postProcessingRuns,
            postProcessingSteps,
            recentEvents,
            BuildWarnings(fragments, currentDuplicates, transcript));
    }

    private async Task<string> EnqueueJobAsync(
        string job,
        Guid meetingId,
        Guid organizationId,
        CancellationToken cancellationToken)
    {
        var tracker = _serviceProvider.GetRequiredService<IPostMeetingProcessingTracker>();

        switch (job)
        {
            case "transcript":
            {
                var pipelineGenerationId = await PostMeetingProcessingPipeline.BeginManualRerunAsync(
                    tracker,
                    organizationId,
                    meetingId,
                    PostMeetingProcessingStepType.Stt,
                    message: "QA transcript rerun enqueued.",
                    cancellationToken: cancellationToken);
                var hangfireJobId = _backgroundJobClient.Enqueue<GenerateMeetingTranscriptJob>(
                    x => x.RunAsync(meetingId, organizationId, pipelineGenerationId, CancellationToken.None));
                await tracker.MarkStepPendingAsync(
                    organizationId,
                    meetingId,
                    PostMeetingProcessingStepType.Stt,
                    pipelineGenerationId,
                    message: "QA transcript rerun enqueued.",
                    relatedHangfireJobId: hangfireJobId,
                    cancellationToken: cancellationToken);
                return hangfireJobId;
            }
            case "summary":
            {
                var pipelineGenerationId = await PostMeetingProcessingPipeline.ResolveAutomaticPipelineGenerationIdAsync(
                    tracker,
                    organizationId,
                    meetingId,
                    cancellationToken);
                return _backgroundJobClient.Enqueue<GenerateMeetingSummaryJob>(
                    x => x.RunAsync(meetingId, organizationId, pipelineGenerationId, CancellationToken.None));
            }
            case "actionItems":
            {
                var pipelineGenerationId = await PostMeetingProcessingPipeline.BeginManualRerunAsync(
                    tracker,
                    organizationId,
                    meetingId,
                    PostMeetingProcessingStepType.ActionExtraction,
                    message: "QA action item extraction rerun enqueued.",
                    cancellationToken: cancellationToken);
                var hangfireJobId = _backgroundJobClient.Enqueue<ExtractActionItemsJob>(
                    x => x.RunAsync(meetingId, organizationId, pipelineGenerationId, CancellationToken.None));
                await tracker.MarkStepPendingAsync(
                    organizationId,
                    meetingId,
                    PostMeetingProcessingStepType.ActionExtraction,
                    pipelineGenerationId,
                    message: "QA action item extraction rerun enqueued.",
                    relatedHangfireJobId: hangfireJobId,
                    cancellationToken: cancellationToken);
                return hangfireJobId;
            }
            case "personalizedSummaries":
            {
                var pipelineGenerationId = await PostMeetingProcessingPipeline.ResolveAutomaticPipelineGenerationIdAsync(
                    tracker,
                    organizationId,
                    meetingId,
                    cancellationToken);
                return _backgroundJobClient.Enqueue<GeneratePersonalizedMeetingSummariesJob>(
                    x => x.RunAsync(meetingId, organizationId, pipelineGenerationId, CancellationToken.None));
            }
            case "rag":
            {
                var pipelineGenerationId = await PostMeetingProcessingPipeline.BeginManualRerunAsync(
                    tracker,
                    organizationId,
                    meetingId,
                    PostMeetingProcessingStepType.KnowledgeIndexing,
                    message: "QA knowledge reindex rerun enqueued.",
                    cancellationToken: cancellationToken);
                var hangfireJobId = _backgroundJobClient.Enqueue<ReindexMeetingKnowledgeJob>(
                    x => x.RunAsync(meetingId, organizationId, pipelineGenerationId, CancellationToken.None));
                await tracker.MarkStepPendingAsync(
                    organizationId,
                    meetingId,
                    PostMeetingProcessingStepType.KnowledgeIndexing,
                    pipelineGenerationId,
                    message: "QA knowledge reindex rerun enqueued.",
                    relatedHangfireJobId: hangfireJobId,
                    cancellationToken: cancellationToken);
                return hangfireJobId;
            }
            default:
                throw new InvalidOperationException($"Unsupported QA job '{job}'.");
        }
    }

    private async Task RunJobInlineAsync(
        string job,
        Guid meetingId,
        Guid organizationId,
        CancellationToken cancellationToken)
    {
        switch (job)
        {
            case "transcript":
            {
                var tracker = _serviceProvider.GetRequiredService<IPostMeetingProcessingTracker>();
                var pipelineGenerationId = await PostMeetingProcessingPipeline.BeginManualRerunAsync(
                    tracker,
                    organizationId,
                    meetingId,
                    PostMeetingProcessingStepType.Stt,
                    message: "QA transcript rerun started inline.",
                    cancellationToken: cancellationToken);
                await _serviceProvider.GetRequiredService<GenerateMeetingTranscriptJob>()
                    .RunAsync(meetingId, organizationId, pipelineGenerationId, cancellationToken);
                break;
            }
            case "summary":
            {
                var tracker = _serviceProvider.GetRequiredService<IPostMeetingProcessingTracker>();
                var pipelineGenerationId = await PostMeetingProcessingPipeline.ResolveAutomaticPipelineGenerationIdAsync(
                    tracker,
                    organizationId,
                    meetingId,
                    cancellationToken);
                await _serviceProvider.GetRequiredService<GenerateMeetingSummaryJob>()
                    .RunAsync(meetingId, organizationId, pipelineGenerationId, cancellationToken);
                break;
            }
            case "actionItems":
            {
                var tracker = _serviceProvider.GetRequiredService<IPostMeetingProcessingTracker>();
                var pipelineGenerationId = await PostMeetingProcessingPipeline.BeginManualRerunAsync(
                    tracker,
                    organizationId,
                    meetingId,
                    PostMeetingProcessingStepType.ActionExtraction,
                    message: "QA action item extraction rerun started inline.",
                    cancellationToken: cancellationToken);
                await ActivatorUtilities.CreateInstance<ExtractActionItemsJob>(_serviceProvider)
                    .RunAsync(meetingId, organizationId, pipelineGenerationId, cancellationToken);
                break;
            }
            case "personalizedSummaries":
            {
                var tracker = _serviceProvider.GetRequiredService<IPostMeetingProcessingTracker>();
                var pipelineGenerationId = await PostMeetingProcessingPipeline.ResolveAutomaticPipelineGenerationIdAsync(
                    tracker,
                    organizationId,
                    meetingId,
                    cancellationToken);
                await _serviceProvider.GetRequiredService<GeneratePersonalizedMeetingSummariesJob>()
                    .RunAsync(meetingId, organizationId, pipelineGenerationId, cancellationToken);
                break;
            }
            case "rag":
            {
                var tracker = _serviceProvider.GetRequiredService<IPostMeetingProcessingTracker>();
                var pipelineGenerationId = await PostMeetingProcessingPipeline.BeginManualRerunAsync(
                    tracker,
                    organizationId,
                    meetingId,
                    PostMeetingProcessingStepType.KnowledgeIndexing,
                    message: "QA knowledge reindex rerun started inline.",
                    cancellationToken: cancellationToken);
                await _serviceProvider.GetRequiredService<ReindexMeetingKnowledgeJob>()
                    .RunAsync(meetingId, organizationId, pipelineGenerationId, cancellationToken);
                break;
            }
            default:
                throw new InvalidOperationException($"Unsupported QA job '{job}'.");
        }
    }

    private async Task WaitForRequestedJobsAsync(
        Guid meetingId,
        Guid organizationId,
        IReadOnlyList<string> jobs,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.Add(timeout);
        while (DateTime.UtcNow < deadline)
        {
            var status = await BuildProcessingStatusAsync(organizationId, meetingId, cancellationToken);
            if (IsSatisfied(status, jobs))
                return;

            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        }
    }

    private static bool IsSatisfied(QaProcessingStatusResponse status, IReadOnlyList<string> jobs)
    {
        return jobs.All(job => job switch
        {
            "transcript" => status.Transcript is not null,
            "summary" => status.Summary is not null,
            "actionItems" => status.ActionItems.Count > 0 || HasTerminalStep(status, "ActionExtraction"),
            "personalizedSummaries" => status.PersonalizedSummaries.Count > 0 || HasTerminalStep(status, "PersonalizedSummaryGeneration"),
            "rag" => status.KnowledgeDocuments.Count > 0 || HasTerminalStep(status, "KnowledgeIndexing"),
            _ => true
        });
    }

    private static bool HasTerminalStep(QaProcessingStatusResponse status, string stepType)
    {
        return status.PostProcessingSteps.Any(step => step.StepType == stepType
                                                      && step.Status is "Completed"
                                                          or "CompletedWithWarnings"
                                                          or "Failed"
                                                          or "Skipped");
    }

    private static bool TryParsePostProcessingStepType(string? value, out PostMeetingProcessingStepType stepType)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            stepType = PostMeetingProcessingStepType.Stt;
            return true;
        }

        return Enum.TryParse(value, ignoreCase: true, out stepType);
    }

    private static bool TryParsePostProcessingStatus(string? value, out PostMeetingProcessingStatus status)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            status = PostMeetingProcessingStatus.Completed;
            return true;
        }

        return Enum.TryParse(value, ignoreCase: true, out status);
    }

    private static IReadOnlyList<string> NormalizeJobs(IReadOnlyList<string>? jobs)
    {
        if (jobs is null || jobs.Count == 0)
            return ["transcript", "summary"];

        var normalized = new List<string>();
        foreach (var job in jobs)
        {
            var value = job.Trim();
            var canonical = value.ToLowerInvariant() switch
            {
                "stt" or "transcribe" or "transcript" => "transcript",
                "summary" or "summarize" => "summary",
                "action" or "actions" or "actionitems" or "action-items" => "actionItems",
                "personalized" or "personalizedsummary" or "personalizedsummaries" or "personalized-summaries" => "personalizedSummaries",
                "rag" or "knowledge" or "knowledgeindexing" or "knowledge-indexing" => "rag",
                _ => throw new InvalidOperationException($"Unsupported QA job '{job}'.")
            };

            if (!normalized.Contains(canonical, StringComparer.Ordinal))
                normalized.Add(canonical);
        }

        return normalized;
    }

    private static IReadOnlyList<string> BuildWarnings(
        IReadOnlyList<QaAudioFragmentStatusResponse> fragments,
        IReadOnlyList<QaKnowledgeDuplicateResponse> duplicateCurrentKnowledgeDocuments,
        MeetingTranscript? transcript)
    {
        var warnings = new List<string>();
        var failedFragments = fragments.Where(x => x.Status == "Failed").ToList();
        if (failedFragments.Count > 0)
        {
            warnings.Add($"{failedFragments.Count} participant audio fragment(s) have terminal audio/storage failures.");
        }

        var retryableSttFragments = fragments.Where(x => x.RetryEligible).ToList();
        if (retryableSttFragments.Count > 0)
        {
            warnings.Add($"{retryableSttFragments.Count} participant audio fragment(s) have retryable STT failures.");
        }

        if (transcript?.CompletenessStatus == MeetingTranscriptCompletenessStatus.CompletedWithWarnings)
        {
            warnings.AddRange(DeserializeStringList(transcript.WarningsJson));
        }

        if (fragments.Count > 0 && transcript is null && failedFragments.Count != fragments.Count)
        {
            warnings.Add("Audio fragments exist but no transcript artifact exists yet.");
        }

        if (duplicateCurrentKnowledgeDocuments.Count > 0)
        {
            warnings.Add($"{duplicateCurrentKnowledgeDocuments.Count} duplicate current knowledge artifact key(s) detected.");
        }

        return warnings;
    }

    private static IReadOnlyList<string> DeserializeStringList(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];

        try
        {
            return JsonSerializer.Deserialize<List<string>>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string ResolveDisplayName(
        IReadOnlyDictionary<Guid, QaParticipantName> participantByUserId,
        Guid participantUserId)
    {
        if (!participantByUserId.TryGetValue(participantUserId, out var participant))
            return participantUserId.ToString();

        return participant.DisplayName ?? participant.Email ?? participant.UserName ?? participantUserId.ToString();
    }

    private static int CountSegments(string segmentsJson)
    {
        if (string.IsNullOrWhiteSpace(segmentsJson))
            return 0;

        try
        {
            using var document = JsonDocument.Parse(segmentsJson);
            return document.RootElement.ValueKind == JsonValueKind.Array
                ? document.RootElement.GetArrayLength()
                : 0;
        }
        catch (JsonException)
        {
            return 0;
        }
    }

    private static string Sha256(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string Preview(string value)
    {
        var normalized = value.ReplaceLineEndings(" ").Trim();
        return normalized.Length <= 500 ? normalized : normalized[..500];
    }

    private static string NormalizeLabel(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? $"user-{Guid.NewGuid():N}"[..13]
            : Slugify(value);
    }

    private static string TruncateForStorage(string value, int maxLength)
    {
        if (maxLength <= 0)
            return string.Empty;

        var trimmed = (value ?? string.Empty).Trim();
        if (trimmed.Length <= maxLength)
            return trimmed;

        var length = maxLength;
        if (length > 0 && char.IsHighSurrogate(trimmed[length - 1]))
            length--;

        return length <= 0
            ? string.Empty
            : trimmed[..length].Trim();
    }

    private static string BuildQaOrganizationSlug(string requestedOrDefault)
    {
        var slug = string.IsNullOrWhiteSpace(requestedOrDefault)
            ? "qa-scenario"
            : Slugify(requestedOrDefault);

        if (string.IsNullOrWhiteSpace(slug))
            slug = "qa-scenario";

        if (slug.Length <= MaxOrganizationSlugLength)
            return slug;

        var hash = ShortHash(slug);
        var prefixLength = Math.Max(0, MaxOrganizationSlugLength - hash.Length - 1);
        var prefix = TruncateForStorage(slug, prefixLength).Trim('-');
        return string.IsNullOrWhiteSpace(prefix)
            ? TruncateForStorage(hash, MaxOrganizationSlugLength)
            : $"{prefix}-{hash}";
    }

    private static string BuildQaGeneratedEmail(string scenarioSlug, string runId, string label)
    {
        var hash = ShortHash($"{scenarioSlug}|{runId}|{label}");
        var shortScenario = TruncateSlugSegment(scenarioSlug, 20);
        var shortRun = TruncateSlugSegment(runId, 24);
        var localBudget = MaxGeneratedEmailLength - GeneratedEmailDomain.Length;
        var labelBudget = localBudget
                          - "qa+".Length
                          - shortScenario.Length
                          - shortRun.Length
                          - hash.Length
                          - 3; // hyphen separators

        if (labelBudget < 1)
        {
            shortRun = TruncateSlugSegment(shortRun, Math.Max(1, shortRun.Length + labelBudget - 1));
            labelBudget = localBudget
                          - "qa+".Length
                          - shortScenario.Length
                          - shortRun.Length
                          - hash.Length
                          - 3;
        }

        var shortLabel = TruncateSlugSegment(label, Math.Max(1, labelBudget));
        var email = $"qa+{shortScenario}-{shortRun}-{shortLabel}-{hash}{GeneratedEmailDomain}";
        return email.Length <= MaxGeneratedEmailLength
            ? email
            : $"qa+{hash}{GeneratedEmailDomain}";
    }

    private static string ShortHash(string value, int chars = 10)
    {
        if (chars <= 0)
            return string.Empty;

        var hash = Sha256(value);
        return hash[..Math.Min(chars, hash.Length)];
    }

    private static string TruncateSlugSegment(string value, int maxLength)
    {
        var slug = Slugify(value);
        var truncated = TruncateForStorage(slug, maxLength).Trim('-');
        return string.IsNullOrWhiteSpace(truncated) ? "qa" : truncated;
    }

    private static string Slugify(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var ch in value.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(ch);
            }
            else if (builder.Length == 0 || builder[^1] != '-')
            {
                builder.Append('-');
            }
        }

        var slug = builder.ToString().Trim('-');
        return string.IsNullOrWhiteSpace(slug) ? "qa" : slug;
    }

    private static bool TryParseKnowledgeArtifactType(string? value, out KnowledgeArtifactType artifactType)
    {
        artifactType = default;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        return Enum.TryParse(value.Trim(), ignoreCase: true, out artifactType);
    }

    private sealed record QaScenarioUserInternal(
        string Label,
        ApplicationUser User,
        OrganizationRole OrganizationRole,
        MeetingRole MeetingRole,
        string Password);

    private sealed record QaParticipantName(
        Guid Id,
        Guid UserId,
        MeetingRole MeetingRole,
        string? DisplayName,
        string? Email,
        string? UserName);
}

public sealed record QaScenarioRequest(
    string? Scenario = null,
    string? RunId = null,
    string? OrganizationName = null,
    string? OrganizationSlug = null,
    IReadOnlyList<QaScenarioUserRequest>? Users = null,
    QaScenarioMeetingRequest? Meeting = null,
    IReadOnlyList<string>? Tags = null);

public sealed record QaScenarioUserRequest(
    string? Label = null,
    string? DisplayName = null,
    OrganizationRole? OrganizationRole = null,
    MeetingRole? MeetingRole = null,
    string? Password = null);

public sealed record QaScenarioMeetingRequest(
    string? Title = null,
    string? Description = null,
    DateTime? ScheduledStartUtc = null,
    DateTime? ScheduledEndUtc = null,
    MeetingStatus? Status = null,
    bool? AiAssistantEnabled = null);

public sealed record QaScenarioResponse(
    string Scenario,
    string RunId,
    Guid OrganizationId,
    string OrganizationName,
    string OrganizationSlug,
    Guid MeetingId,
    string MeetingTitle,
    string LiveKitRoomName,
    IReadOnlyDictionary<string, QaScenarioUserResponse> Users,
    IReadOnlyDictionary<string, QaScenarioJoinTokenResponse> JoinTokens,
    IReadOnlyList<QaScenarioTagResponse> Tags);

public sealed record QaScenarioUserResponse(
    Guid UserId,
    string Email,
    string DisplayName,
    string OrganizationRole,
    string MeetingRole,
    string AccessToken,
    int ExpiresIn);

public sealed record QaScenarioJoinTokenResponse(
    string AccessToken,
    string RoomName,
    string ServerUrl,
    DateTime ExpiresAtUtc,
    SessionPermissions Permissions);

public sealed record QaScenarioTagResponse(Guid Id, string Name);

public sealed record QaCreateAdditionalMeetingRequest(
    Guid SourceMeetingId,
    string? Title = null,
    string? Description = null,
    DateTime? ScheduledStartUtc = null,
    DateTime? ScheduledEndUtc = null,
    MeetingStatus? Status = null,
    bool? AiAssistantEnabled = null,
    bool? CopyParticipants = null,
    bool? CopyTags = null,
    bool? LinkRecurringSeries = null,
    int? RecurringOccurrenceIndex = null);

public sealed record QaAdditionalMeetingResponse(
    Guid OrganizationId,
    Guid SourceMeetingId,
    Guid MeetingId,
    string MeetingTitle,
    string LiveKitRoomName,
    IReadOnlyList<QaAdditionalMeetingParticipantResponse> Participants,
    IReadOnlyList<QaScenarioTagResponse> Tags,
    DateTime ScheduledStartUtc,
    DateTime ScheduledEndUtc,
    Guid? RecurringSeriesId,
    int? RecurringOccurrenceIndex);

public sealed record QaAdditionalMeetingParticipantResponse(
    Guid MeetingParticipantId,
    Guid UserId,
    string MeetingRole);

public sealed record QaMintAgentTokenRequest(Guid OrganizationId, int? ExpiresInMinutes = null);

public sealed record QaAgentTokenResponse(
    Guid OrganizationId,
    Guid MeetingId,
    string TokenType,
    string AccessToken,
    DateTime ExpiresAtUtc,
    int ExpiresInMinutes);

public sealed record QaReminderStatusResponse(
    Guid Id,
    Guid OrganizationId,
    Guid? MeetingId,
    string Text,
    string Scope,
    string Channel,
    string Status,
    DateTime ReminderAtUtc,
    DateTime? DeliveredAtUtc,
    Guid? CreatedByUserId,
    Guid? TargetUserId,
    Guid? SourceMeetingRecurringSeriesId,
    int? SourceMeetingRecurringOccurrenceIndex,
    DateTime? SourceMeetingScheduledStartUtc);

public sealed class QaAudioFragmentUploadRequest
{
    public Guid OrganizationId { get; set; }
    public Guid ParticipantUserId { get; set; }
    public string? TrackSid { get; set; }
    public string? StorageObjectKey { get; set; }
    public DateTime? TrackPublishedAtUtc { get; set; }
    public DateTime? EgressStartedAtUtc { get; set; }
    public DateTime? EgressEndedAtUtc { get; set; }
    public double? DurationSeconds { get; set; }
    public IFormFile? File { get; set; }
}

public sealed record QaAudioFragmentResponse(
    Guid FragmentId,
    Guid TrackId,
    Guid MeetingId,
    Guid OrganizationId,
    Guid ParticipantUserId,
    string TrackSid,
    string StorageObjectKey,
    string StorageLocation,
    long? SizeBytes,
    string Status);

public sealed record QaProcessRequest(
    Guid OrganizationId,
    IReadOnlyList<string>? Jobs = null,
    string? Mode = "inline",
    int? TimeoutSeconds = null);

public sealed record QaProcessResponse(
    string Mode,
    DateTime StartedAtUtc,
    DateTime CompletedAtUtc,
    IReadOnlyList<QaProcessJobResponse> Jobs,
    QaProcessingStatusResponse Status);

public sealed record QaProcessJobResponse(
    string Job,
    string Status,
    string? HangfireJobId,
    DateTime? StartedAtUtc,
    DateTime? CompletedAtUtc);

public sealed record QaProcessingStatusResponse(
    QaMeetingStatusResponse Meeting,
    IReadOnlyList<QaParticipantStatusResponse> Participants,
    IReadOnlyList<QaAudioTrackStatusResponse> AudioTracks,
    IReadOnlyList<QaAudioFragmentStatusResponse> AudioFragments,
    QaTranscriptStatusResponse? Transcript,
    QaSummaryStatusResponse? Summary,
    IReadOnlyList<QaPersonalizedSummaryStatusResponse> PersonalizedSummaries,
    IReadOnlyList<QaActionItemStatusResponse> ActionItems,
    IReadOnlyList<QaKnowledgeDocumentStatusResponse> KnowledgeDocuments,
    IReadOnlyList<QaKnowledgeDuplicateResponse> DuplicateCurrentKnowledgeDocuments,
    IReadOnlyList<QaPostProcessingRunStatusResponse> PostProcessingRuns,
    IReadOnlyList<QaPostProcessingStepStatusResponse> PostProcessingSteps,
    IReadOnlyList<QaPostProcessingEventStatusResponse> RecentPostProcessingEvents,
    IReadOnlyList<string> Warnings);

public sealed record QaMeetingStatusResponse(
    Guid Id,
    Guid OrganizationId,
    string Title,
    string Status,
    DateTime? RoomActivatedAtUtc,
    bool AiAssistantEnabled);

public sealed record QaParticipantStatusResponse(
    Guid MeetingParticipantId,
    Guid UserId,
    string DisplayName,
    string MeetingRole);

public sealed record QaAudioTrackStatusResponse(
    Guid Id,
    Guid ParticipantUserId,
    string? ParticipantDisplayName,
    string Status,
    string? StorageObjectKey,
    long? SizeBytes,
    double? DurationSeconds);

public sealed record QaAudioFragmentStatusResponse(
    Guid Id,
    Guid? ParticipantAudioTrackId,
    Guid? ParticipantUserId,
    string? ParticipantDisplayName,
    string TrackSid,
    string Status,
    string SttStatus,
    string? StorageObjectKey,
    string? StorageLocation,
    long? SizeBytes,
    double? DurationSeconds,
    DateTime? TrackPublishedAtUtc,
    DateTime? StorageAvailableAtUtc,
    DateTime? FailedAtUtc,
    string? FailureCode,
    string? FailureMessage,
    int SttAttemptCount,
    DateTime? LastSttAttemptAtUtc,
    DateTime? LastSttSucceededAtUtc,
    DateTime? LastSttFailedAtUtc,
    string? SttFailureCode,
    string? SttFailureMessage,
    string? SttModel,
    int SttSegmentCount,
    bool RetryEligible);

public sealed record QaTranscriptStatusResponse(
    Guid Id,
    DateTime GeneratedAtUtc,
    string SttModel,
    int TextLength,
    int SegmentCount,
    string FullTextSha256,
    string TranscriptHash,
    int TranscriptRevision,
    string Preview,
    string CompletenessStatus,
    bool IsDegraded,
    int ExpectedAudioFragmentCount,
    int TranscribedAudioFragmentCount,
    int RetryableFailedAudioFragmentCount,
    int TerminalFailedAudioFragmentCount,
    IReadOnlyList<string> MissingAudioFragmentIds,
    IReadOnlyList<string> Warnings);

public sealed record QaSummaryStatusResponse(
    Guid Id,
    DateTime GeneratedAtUtc,
    string LlmModel,
    int TextLength,
    string SummarySha256,
    string Preview,
    Guid? SourceTranscriptId = null,
    string? SourceTranscriptHash = null,
    int? SourceTranscriptRevision = null,
    DateTime? SourceTranscriptGeneratedAtUtc = null);

public sealed record QaPersonalizedSummaryStatusResponse(
    Guid Id,
    Guid UserId,
    string DisplayName,
    string Status,
    DateTime? GeneratedAtUtc,
    string? EligibilityReason,
    int SummaryTextLength,
    string? SummarySha256 = null,
    string Preview = "",
    Guid? SourceTranscriptId = null,
    string? SourceTranscriptHash = null,
    int? SourceTranscriptRevision = null,
    DateTime? SourceTranscriptGeneratedAtUtc = null);

public sealed record QaActionItemStatusResponse(
    Guid Id,
    string Title,
    string Status,
    Guid? AssignedToUserId,
    DateTime? DueDateUtc,
    DateTime ExtractedAtUtc,
    string? DescriptionSha256 = null,
    string Preview = "",
    Guid? SourceTranscriptId = null,
    string? SourceTranscriptHash = null,
    int? SourceTranscriptRevision = null,
    DateTime? SourceTranscriptGeneratedAtUtc = null,
    DateTime? SupersededAtUtc = null);

public sealed record QaSourceRevisionActionItemRequest(
    string Title,
    string? Description = null,
    Guid? AssignedToUserId = null);

public sealed record QaSourceRevisionPersonalizedSummaryRequest(
    Guid UserId,
    string SummaryText,
    string? EligibilityReason = null);

public sealed record QaSourceRevisionFixtureRequest(
    Guid OrganizationId,
    string SourceLabel,
    string TranscriptText,
    string SummaryText,
    IReadOnlyList<QaSourceRevisionActionItemRequest>? ActionItems = null,
    IReadOnlyList<QaSourceRevisionPersonalizedSummaryRequest>? PersonalizedSummaries = null,
    bool MarkPostProcessingCompleted = true);

public sealed record QaSourceRevisionFixtureResponse(
    Guid OrganizationId,
    Guid MeetingId,
    string SourceLabel,
    Guid TranscriptId,
    Guid SummaryId,
    IReadOnlyList<Guid> ActionItemIds,
    IReadOnlyList<Guid> PersonalizedSummaryIds,
    Guid? PipelineGenerationId,
    Guid? PostProcessingRunId);

public sealed record QaSourceRevisionTranscriptRequest(
    Guid OrganizationId,
    string SourceLabel,
    string TranscriptText,
    bool BeginPipelineGeneration = true);

public sealed record QaSourceRevisionTranscriptResponse(
    Guid OrganizationId,
    Guid MeetingId,
    string SourceLabel,
    Guid TranscriptId,
    string? PreviousFullTextSha256,
    string CurrentFullTextSha256,
    string TranscriptHash,
    int TranscriptRevision,
    bool TranscriptChanged,
    Guid? PipelineGenerationId,
    Guid? PostProcessingRunId);

public sealed record QaKnowledgeDocumentStatusResponse(
    Guid Id,
    string ArtifactType,
    Guid ArtifactId,
    int ArtifactVersion,
    bool IsCurrent,
    string Visibility,
    string ContentHash,
    Guid IndexGenerationId,
    DateTime GeneratedAtUtc,
    Guid? SourceTranscriptId = null,
    string? SourceTranscriptHash = null,
    int? SourceTranscriptRevision = null,
    DateTime? SourceTranscriptGeneratedAtUtc = null);

public sealed record QaKnowledgeDuplicateResponse(
    string ArtifactType,
    Guid ArtifactId,
    int ArtifactVersion,
    IReadOnlyList<Guid> DocumentIds);

public sealed record QaPostProcessingRunStatusResponse(
    Guid Id,
    string Status,
    DateTime? StartedAtUtc,
    DateTime? CompletedAtUtc,
    DateTime? FailedAtUtc,
    string? RelatedHangfireJobId,
    Guid PipelineGenerationId);

public sealed record QaPostProcessingStepStatusResponse(
    Guid Id,
    string StepType,
    string Status,
    int AttemptCount,
    DateTime? StartedAtUtc,
    DateTime? CompletedAtUtc,
    DateTime? FailedAtUtc,
    string? RelatedHangfireJobId,
    string? ErrorCode,
    string? ErrorMessage);

public sealed record QaPostProcessingEventStatusResponse(
    Guid Id,
    string? StepType,
    string EventType,
    string? Status,
    string Message,
    string? RelatedHangfireJobId,
    string? ErrorCode,
    string? ErrorMessage,
    DateTime CreatedAtUtc);

public sealed record QaStalePostProcessingTrackerStateRequest(
    Guid OrganizationId,
    string? StepType = "Stt",
    string? OldHangfireJobId = "qa-old-stt-job",
    string? Status = "Completed",
    bool CompleteRun = true);

public sealed record QaStalePostProcessingTrackerStateResponse(
    Guid OrganizationId,
    Guid MeetingId,
    Guid RunId,
    Guid PipelineGenerationId,
    Guid StepId,
    string OldHangfireJobId,
    string StepType,
    string StepStatus,
    string RunStatus,
    IReadOnlyList<Guid> FragmentIds);

public sealed record QaRagFixtureArtifactsRequest(
    Guid OrganizationId,
    string TranscriptText,
    string SummaryText,
    IReadOnlyList<string>? ActionItems = null);

public sealed record QaRagFixtureArtifactsResponse(
    Guid OrganizationId,
    Guid MeetingId,
    string MeetingStatus,
    Guid TranscriptId,
    Guid SummaryId,
    int ActionItemCount);

public sealed record QaRagDuplicateCurrentRequest(
    Guid OrganizationId,
    string ArtifactType,
    int ArtifactVersion = 1);

public sealed record QaRagDuplicateCurrentResponse(
    Guid OrganizationId,
    Guid MeetingId,
    Guid SourceDocumentId,
    Guid DuplicateDocumentId,
    string ArtifactType,
    Guid ArtifactId,
    int ArtifactVersion,
    int DuplicateCurrentCount);

public sealed record QaMeetingTranscriptPreviewResponse(
    Guid OrganizationId,
    Guid MeetingId,
    string FullText,
    string SegmentsJson,
    string SttModel,
    string CompletenessStatus,
    IReadOnlyList<string> Warnings,
    int ExpectedAudioFragmentCount,
    int TranscribedAudioFragmentCount,
    int RetryableFailedAudioFragmentCount,
    int TerminalFailedAudioFragmentCount,
    DateTime GeneratedAtUtc,
    Guid? ExistingTranscriptId,
    string? ExistingTranscriptHash,
    int? ExistingTranscriptRevision,
    string PreviewTranscriptHash,
    int PreviewTranscriptRevision,
    bool WouldChangeExistingTranscript);
