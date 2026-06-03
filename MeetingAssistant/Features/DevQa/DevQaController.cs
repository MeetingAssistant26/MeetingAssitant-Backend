using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Hangfire;
using MeetingAssistant.Features.ActionItems.Jobs;
using MeetingAssistant.Features.ActionItems.Models.Entities;
using MeetingAssistant.Features.Identity.Entites;
using MeetingAssistant.Features.Identity.Services;
using MeetingAssistant.Features.LiveSession.Jobs;
using MeetingAssistant.Features.LiveSession.Models;
using MeetingAssistant.Features.LiveSession.Models.PostProcessing;
using MeetingAssistant.Features.LiveSession.Services;
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
    IBackgroundJobClient backgroundJobClient,
    IServiceProvider serviceProvider,
    IHostEnvironment environment,
    IConfiguration configuration,
    ILogger<DevQaController> logger) : ControllerBase
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly ApplicationDbContext _dbContext = dbContext;
    private readonly UserManager<ApplicationUser> _userManager = userManager;
    private readonly ITokenService _tokenService = tokenService;
    private readonly ILiveKitTokenIssuer _liveKitTokenIssuer = liveKitTokenIssuer;
    private readonly IStorageService _storageService = storageService;
    private readonly IBackgroundJobClient _backgroundJobClient = backgroundJobClient;
    private readonly IServiceProvider _serviceProvider = serviceProvider;
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
        var orgSlug = Slugify(request.OrganizationSlug ?? $"{scenarioSlug}-{runId}");
        var orgName = string.IsNullOrWhiteSpace(request.OrganizationName)
            ? $"QA {scenarioName} {runId}"
            : request.OrganizationName!.Trim();

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
            var email = $"qa+{scenarioSlug}-{runId}-{label}@meetingassistant.local";
            var displayName = string.IsNullOrWhiteSpace(requestedUser.DisplayName)
                ? $"QA {label}"
                : requestedUser.DisplayName!.Trim();
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

            var normalizedTagName = tagName.Trim();
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
            Title = string.IsNullOrWhiteSpace(meetingRequest.Title)
                ? $"QA {scenarioName} {runId}"
                : meetingRequest.Title!.Trim(),
            Description = meetingRequest.Description ?? $"Autonomous QA scenario {scenarioName} created at {now:O}.",
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
                var hangfireJobId = EnqueueJob(job, meetingId, request.OrganizationId);
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
                x.StorageObjectKey,
                x.StorageLocation,
                x.SizeBytes,
                x.DurationSeconds,
                x.TrackPublishedAtUtc,
                x.StorageAvailableAtUtc,
                x.FailedAtUtc,
                x.FailureCode,
                x.FailureMessage,
                x.Status == ParticipantAudioFragmentStatus.Failed
                && x.FailureCode == "stt_failed"
                && x.StorageObjectKey != null))
            .ToListAsync(cancellationToken);

        fragments = fragments
            .Select(fragment => fragment with
            {
                ParticipantDisplayName = ResolveDisplayName(participantByUserId, fragment.ParticipantUserId)
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

        var personalized = await _dbContext.PersonalizedMeetingSummaries
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(x => x.MeetingId == meetingId && x.OrganizationId == organizationId)
            .OrderBy(x => x.User.DisplayName)
            .Select(x => new QaPersonalizedSummaryStatusResponse(
                x.Id,
                x.UserId,
                x.User.DisplayName ?? x.User.Email ?? x.UserId.ToString(),
                x.Status.ToString(),
                x.GeneratedAtUtc,
                x.EligibilityReason,
                x.SummaryText == null ? 0 : x.SummaryText.Length))
            .ToListAsync(cancellationToken);

        var actionItems = await _dbContext.ActionItems
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(x => x.MeetingId == meetingId && x.OrganizationId == organizationId)
            .OrderBy(x => x.CreatedAtUtc)
            .Select(x => new QaActionItemStatusResponse(
                x.Id,
                x.Title,
                x.Status.ToString(),
                x.AssignedToUserId,
                x.DueDateUtc,
                x.ExtractedAtUtc))
            .ToListAsync(cancellationToken);

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
                x.GeneratedAtUtc))
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
                    Preview(transcript.FullText)),
            summary is null
                ? null
                : new QaSummaryStatusResponse(
                    summary.Id,
                    summary.GeneratedAtUtc,
                    summary.LlmModel,
                    summary.SummaryText.Length,
                    Sha256(summary.SummaryText),
                    Preview(summary.SummaryText)),
            personalized,
            actionItems,
            knowledgeDocuments,
            currentDuplicates,
            postProcessingRuns,
            postProcessingSteps,
            recentEvents,
            BuildWarnings(fragments, currentDuplicates, transcript));
    }

    private string EnqueueJob(string job, Guid meetingId, Guid organizationId)
    {
        return job switch
        {
            "transcript" => _backgroundJobClient.Enqueue<GenerateMeetingTranscriptJob>(x => x.RunAsync(meetingId, organizationId, CancellationToken.None)),
            "summary" => _backgroundJobClient.Enqueue<GenerateMeetingSummaryJob>(x => x.RunAsync(meetingId, organizationId, CancellationToken.None)),
            "actionItems" => _backgroundJobClient.Enqueue<ExtractActionItemsJob>(x => x.RunAsync(meetingId, organizationId, CancellationToken.None)),
            "personalizedSummaries" => _backgroundJobClient.Enqueue<GeneratePersonalizedMeetingSummariesJob>(x => x.RunAsync(meetingId, organizationId, CancellationToken.None)),
            "rag" => _backgroundJobClient.Enqueue<ReindexMeetingKnowledgeJob>(x => x.RunAsync(meetingId, organizationId, CancellationToken.None)),
            _ => throw new InvalidOperationException($"Unsupported QA job '{job}'.")
        };
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
                await _serviceProvider.GetRequiredService<GenerateMeetingTranscriptJob>()
                    .RunAsync(meetingId, organizationId, cancellationToken);
                break;
            case "summary":
                await _serviceProvider.GetRequiredService<GenerateMeetingSummaryJob>()
                    .RunAsync(meetingId, organizationId, cancellationToken);
                break;
            case "actionItems":
                await ActivatorUtilities.CreateInstance<ExtractActionItemsJob>(_serviceProvider)
                    .RunAsync(meetingId, organizationId, cancellationToken);
                break;
            case "personalizedSummaries":
                await _serviceProvider.GetRequiredService<GeneratePersonalizedMeetingSummariesJob>()
                    .RunAsync(meetingId, organizationId, cancellationToken);
                break;
            case "rag":
                await _serviceProvider.GetRequiredService<ReindexMeetingKnowledgeJob>()
                    .RunAsync(meetingId, organizationId, cancellationToken);
                break;
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
                                                      && step.Status is "Completed" or "Failed" or "Skipped");
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
            warnings.Add($"{failedFragments.Count} participant audio fragment(s) are failed; transcript may be partial.");
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
    Guid ParticipantUserId,
    string? ParticipantDisplayName,
    string TrackSid,
    string Status,
    string? StorageObjectKey,
    string? StorageLocation,
    long? SizeBytes,
    double? DurationSeconds,
    DateTime? TrackPublishedAtUtc,
    DateTime? StorageAvailableAtUtc,
    DateTime? FailedAtUtc,
    string? FailureCode,
    string? FailureMessage,
    bool RetryEligible);

public sealed record QaTranscriptStatusResponse(
    Guid Id,
    DateTime GeneratedAtUtc,
    string SttModel,
    int TextLength,
    int SegmentCount,
    string FullTextSha256,
    string Preview);

public sealed record QaSummaryStatusResponse(
    Guid Id,
    DateTime GeneratedAtUtc,
    string LlmModel,
    int TextLength,
    string SummarySha256,
    string Preview);

public sealed record QaPersonalizedSummaryStatusResponse(
    Guid Id,
    Guid UserId,
    string DisplayName,
    string Status,
    DateTime? GeneratedAtUtc,
    string? EligibilityReason,
    int SummaryTextLength);

public sealed record QaActionItemStatusResponse(
    Guid Id,
    string Title,
    string Status,
    Guid? AssignedToUserId,
    DateTime? DueDateUtc,
    DateTime ExtractedAtUtc);

public sealed record QaKnowledgeDocumentStatusResponse(
    Guid Id,
    string ArtifactType,
    Guid ArtifactId,
    int ArtifactVersion,
    bool IsCurrent,
    string Visibility,
    string ContentHash,
    Guid IndexGenerationId,
    DateTime GeneratedAtUtc);

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
