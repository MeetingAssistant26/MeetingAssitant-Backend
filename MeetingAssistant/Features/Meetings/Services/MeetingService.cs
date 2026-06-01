using Mapster;
using MeetingAssistant.Api.Infrastructure.Services;
using MeetingAssistant.Features.LiveSession.Infrastructure;
using MeetingAssistant.Features.Meetings.Contracts.Requests;
using MeetingAssistant.Features.Meetings.Contracts.Responses;
using MeetingAssistant.Features.Meetings.Models;
using MeetingAssistant.Features.Meetings.Models.Events;
using MeetingAssistant.Features.Organizations.Models;
using MeetingAssistant.Infrastructure.Persistence.DbContext;
using MeetingAssistant.Shared.Abstractions;
using MeetingAssistant.Shared.Errors;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
namespace MeetingAssistant.Features.Meetings.Services
{
    public class MeetingService(
        ApplicationDbContext dbContext,
        ITenantProvider tenantProvider,
        IOptions<LiveKitOptions> liveKitOptions,
        IMeetingConflictService meetingConflictService) : IMeetingService
    {
        private readonly ApplicationDbContext _dbContext = dbContext;
        private readonly ITenantProvider _tenantProvider = tenantProvider;
        private readonly LiveKitOptions _liveKitOptions = liveKitOptions.Value;
        private readonly IMeetingConflictService _meetingConflictService = meetingConflictService;

        public async Task<Result<MeetingResponse>> CreateMeetingAsync(
            CreateMeetingRequest request,
            Guid userId,
            CancellationToken cancellationToken = default)
        {
            var orgId = GetOrganizationId();

            var membership = await _dbContext.UserOrgMemberships
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(m => m.UserId == userId && m.OrganizationId == orgId && m.IsEnabled, cancellationToken);

            if (membership == null)
                return Result.Failure<MeetingResponse>(MeetingErrors.NotOrgMember);

            if (membership.OrgRole == OrganizationRole.Guest)
                return Result.Failure<MeetingResponse>(MeetingErrors.GuestNotAllowed);

            var requestedParticipants = request.Participants ?? [];
            var requestedParticipantIds = requestedParticipants.Select(p => p.UserId).Distinct().ToList();

            if (requestedParticipantIds.Contains(userId))
                return Result.Failure<MeetingResponse>(MeetingErrors.AlreadyParticipant);

            if (requestedParticipantIds.Count > 0)
            {
                var activeParticipantMemberCount = await _dbContext.UserOrgMemberships
                    .IgnoreQueryFilters()
                    .CountAsync(
                        m => requestedParticipantIds.Contains(m.UserId)
                             && m.OrganizationId == orgId
                             && m.IsEnabled,
                        cancellationToken);

                if (activeParticipantMemberCount != requestedParticipantIds.Count)
                    return Result.Failure<MeetingResponse>(MeetingErrors.NotOrgMember);
            }

            var conflictUserIds = requestedParticipantIds.Append(userId).ToList();
            var conflicts = await _meetingConflictService.FindConflictsAsync(
                orgId,
                conflictUserIds,
                request.ScheduledStartUtc,
                request.ScheduledEndUtc,
                null,
                cancellationToken);

            if (conflicts.Count > 0)
                return Result.Failure<MeetingResponse>(MeetingErrors.ConflictDetectedWithDetails(conflicts));

            var meeting = new Meeting
            {
                OrganizationId = orgId,
                Title = request.Title,
                Description = request.Description,
                ScheduledStartUtc = request.ScheduledStartUtc,
                ScheduledEndUtc = request.ScheduledEndUtc,
                Status = MeetingStatus.Scheduled,
                AiAssistantEnabled = _liveKitOptions.AiAssistantDefaultEnabled
            };

            // Auto-add creator as Host
            var hostParticipant = new MeetingParticipant
            {
                MeetingId = meeting.Id,
                OrganizationId = orgId,
                UserId = userId,
                MeetingRole = MeetingRole.Host
            };
            meeting.Participants.Add(hostParticipant);

            foreach (var participant in requestedParticipants)
            {
                meeting.Participants.Add(new MeetingParticipant
                {
                    MeetingId = meeting.Id,
                    OrganizationId = orgId,
                    UserId = participant.UserId,
                    MeetingRole = participant.MeetingRole
                });
            }

            // Associate tags if provided
            if (request.TagIds is { Count: > 0 })
            {
                var tagValidationResult = await ValidateAndAssociateTagsAsync(meeting, request.TagIds, orgId, cancellationToken);
                if (tagValidationResult.IsFailure)
                    return Result.Failure<MeetingResponse>(tagValidationResult.Error);
            }

            meeting.RaiseDomainEvent(new MeetingCreatedEvent(orgId, meeting.Id));

            _dbContext.Meetings.Add(meeting);
            await _dbContext.SaveChangesAsync(cancellationToken);

            return Result.Success(await LoadMeetingResponseAsync(meeting.Id, cancellationToken));
        }

        public async Task<Result<MeetingResponse>> UpdateMeetingAsync(
            Guid meetingId,
            UpdateMeetingRequest request,
            Guid userId,
            CancellationToken cancellationToken = default)
        {
            var orgId = GetOrganizationId();

            var meeting = await _dbContext.Meetings
                .Include(m => m.Participants).ThenInclude(p => p.User)
                .Include(m => m.Tags)
                .FirstOrDefaultAsync(m => m.Id == meetingId, cancellationToken);

            if (meeting == null)
                return Result.Failure<MeetingResponse>(MeetingErrors.NotFound);

            var callerParticipant = meeting.Participants.FirstOrDefault(p => p.UserId == userId);
            if (callerParticipant == null || callerParticipant.MeetingRole != MeetingRole.Host)
                return Result.Failure<MeetingResponse>(MeetingErrors.NotHost);

            if (meeting.Status != MeetingStatus.Scheduled)
                return Result.Failure<MeetingResponse>(MeetingErrors.InvalidStatus);

            if (request.ScheduledStartUtc.HasValue || request.ScheduledEndUtc.HasValue)
            {
                var nextStartUtc = request.ScheduledStartUtc ?? meeting.ScheduledStartUtc;
                var nextEndUtc = request.ScheduledEndUtc ?? meeting.ScheduledEndUtc;
                var participantUserIds = meeting.Participants.Select(p => p.UserId).ToList();

                var conflicts = await _meetingConflictService.FindConflictsAsync(
                    orgId,
                    participantUserIds,
                    nextStartUtc,
                    nextEndUtc,
                    meetingId,
                    cancellationToken);

                if (conflicts.Count > 0)
                    return Result.Failure<MeetingResponse>(MeetingErrors.ConflictDetectedWithDetails(conflicts));
            }

            if (request.Title != null)
                meeting.Title = request.Title;

            if (request.Description != null)
                meeting.Description = request.Description;

            if (request.ScheduledStartUtc.HasValue)
                meeting.ScheduledStartUtc = request.ScheduledStartUtc.Value;

            if (request.ScheduledEndUtc.HasValue)
                meeting.ScheduledEndUtc = request.ScheduledEndUtc.Value;

            // Replace tag associations if tagIds provided
            if (request.TagIds != null)
            {
                // Remove existing tags
                _dbContext.MeetingMeetingTags.RemoveRange(meeting.Tags);
                meeting.Tags.Clear();

                if (request.TagIds.Count > 0)
                {
                    var tagValidationResult = await ValidateAndAssociateTagsAsync(meeting, request.TagIds, orgId, cancellationToken);
                    if (tagValidationResult.IsFailure)
                        return Result.Failure<MeetingResponse>(tagValidationResult.Error);
                }
            }

            meeting.RaiseDomainEvent(new MeetingUpdatedEvent(orgId, meeting.Id));
            await _dbContext.SaveChangesAsync(cancellationToken);

            return Result.Success(await LoadMeetingResponseAsync(meeting.Id, cancellationToken));
        }

        public async Task<Result<ConflictCheckResponse>> CheckSchedulingConflictsAsync(
            MeetingConflictCheckRequest request,
            Guid userId,
            CancellationToken cancellationToken = default)
        {
            var orgId = GetOrganizationId();

            var membership = await _dbContext.UserOrgMemberships
                .IgnoreQueryFilters()
                .Where(m => m.UserId == userId && m.OrganizationId == orgId && m.IsEnabled)
                .Select(m => (OrganizationRole?)m.OrgRole)
                .FirstOrDefaultAsync(cancellationToken);

            if (membership == null)
                return Result.Failure<ConflictCheckResponse>(MeetingErrors.NotOrgMember);

            if (membership == OrganizationRole.Guest)
                return Result.Failure<ConflictCheckResponse>(MeetingErrors.GuestNotAllowed);

            List<Guid>? existingMeetingParticipantIds = null;
            if (request.ExcludeMeetingId.HasValue)
            {
                var excludedMeeting = await _dbContext.Meetings
                    .Where(m => m.Id == request.ExcludeMeetingId.Value && m.OrganizationId == orgId)
                    .Select(m => new
                    {
                        CallerRole = m.Participants
                            .Where(p => p.UserId == userId)
                            .Select(p => (MeetingRole?)p.MeetingRole)
                            .FirstOrDefault(),
                        Participants = m.Participants.Select(p => p.UserId).ToList()
                    })
                    .FirstOrDefaultAsync(cancellationToken);

                if (excludedMeeting == null)
                    return Result.Failure<ConflictCheckResponse>(MeetingErrors.NotFound);

                if (excludedMeeting.CallerRole != MeetingRole.Host && excludedMeeting.CallerRole != MeetingRole.CoHost)
                    return Result.Failure<ConflictCheckResponse>(MeetingErrors.NotHost);

                existingMeetingParticipantIds = excludedMeeting.Participants;
            }

            var participantUserIds = request.ParticipantUserIds is { Count: > 0 }
                ? request.ParticipantUserIds
                : existingMeetingParticipantIds ?? [];

            var userIdsToCheck = participantUserIds
                .Append(userId)
                .Where(id => id != Guid.Empty)
                .Distinct()
                .ToList();

            var activeMemberCount = await _dbContext.UserOrgMemberships
                .IgnoreQueryFilters()
                .CountAsync(
                    m => userIdsToCheck.Contains(m.UserId)
                         && m.OrganizationId == orgId
                         && m.IsEnabled,
                    cancellationToken);

            if (activeMemberCount != userIdsToCheck.Count)
                return Result.Failure<ConflictCheckResponse>(MeetingErrors.NotOrgMember);

            var conflicts = await _meetingConflictService.FindConflictsAsync(
                orgId,
                userIdsToCheck,
                request.ScheduledStartUtc,
                request.ScheduledEndUtc,
                request.ExcludeMeetingId,
                cancellationToken);

            return Result.Success(new ConflictCheckResponse(conflicts.ToList()));
        }

        public async Task<Result> CancelMeetingAsync(
            Guid meetingId,
            Guid userId,
            CancellationToken cancellationToken = default)
        {
            var orgId = GetOrganizationId();

            var meeting = await _dbContext.Meetings
                .Include(m => m.Participants).ThenInclude(p => p.User)
                .FirstOrDefaultAsync(m => m.Id == meetingId, cancellationToken);

            if (meeting == null)
                return Result.Failure(MeetingErrors.NotFound);

            var callerParticipant = meeting.Participants.FirstOrDefault(p => p.UserId == userId);
            if (callerParticipant == null || callerParticipant.MeetingRole != MeetingRole.Host)
                return Result.Failure(MeetingErrors.NotHost);

            if (meeting.Status != MeetingStatus.Scheduled)
                return Result.Failure(MeetingErrors.InvalidStatus);

            meeting.Status = MeetingStatus.Cancelled;

            meeting.RaiseDomainEvent(new MeetingCancelledEvent(orgId, meeting.Id));
            await _dbContext.SaveChangesAsync(cancellationToken);

            return Result.Success();
        }

        public async Task<Result<MeetingListResponse>> ListMeetingsAsync(
            string? filter,
            int page,
            int pageSize,
            CancellationToken cancellationToken = default)
        {
            var now = DateTime.UtcNow;

            IQueryable<Meeting> query = _dbContext.Meetings
                .Include(m => m.Participants).ThenInclude(p => p.User)
                .Include(m => m.Tags);

            if (string.Equals(filter, "upcoming", StringComparison.OrdinalIgnoreCase))
            {
                query = query
                    .Where(m => m.ScheduledStartUtc > now
                             && (m.Status == MeetingStatus.Scheduled || m.Status == MeetingStatus.InProgress))
                    .OrderBy(m => m.ScheduledStartUtc);
            }
            else if (string.Equals(filter, "past", StringComparison.OrdinalIgnoreCase))
            {
                query = query
                    .Where(m => m.Status == MeetingStatus.Completed
                             || m.Status == MeetingStatus.Cancelled
                             || m.ScheduledEndUtc < now)
                    .OrderByDescending(m => m.ScheduledStartUtc);
            }
            else
            {
                query = query.OrderByDescending(m => m.ScheduledStartUtc);
            }

            var totalCount = await query.CountAsync(cancellationToken);

            var meetings = await query
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync(cancellationToken);

            var items = meetings.Adapt<List<MeetingResponse>>();

            return Result.Success(new MeetingListResponse(items, totalCount, page, pageSize));
        }

        public async Task<Result<MeetingResponse>> GetMeetingAsync(
            Guid meetingId,
            CancellationToken cancellationToken = default)
        {
            var exists = await _dbContext.Meetings
                .AsNoTracking()
                .AnyAsync(m => m.Id == meetingId, cancellationToken);

            if (!exists)
                return Result.Failure<MeetingResponse>(MeetingErrors.NotFound);

            return Result.Success(await LoadMeetingResponseAsync(meetingId, cancellationToken));
        }

        public async Task<Result<MeetingResponse>> StartMeetingAsync(
            Guid meetingId,
            Guid userId,
            CancellationToken cancellationToken = default)
        {
            var orgId = GetOrganizationId();

            var meeting = await _dbContext.Meetings
                .Include(m => m.Participants)
                .FirstOrDefaultAsync(m => m.Id == meetingId, cancellationToken);

            if (meeting == null)
                return Result.Failure<MeetingResponse>(MeetingErrors.NotFound);

            var authorizationResult = await EnsureLifecycleControlAllowedAsync(meeting, orgId, userId, cancellationToken);
            if (authorizationResult.IsFailure)
                return Result.Failure<MeetingResponse>(authorizationResult.Error);

            if (meeting.Status == MeetingStatus.Scheduled)
            {
                meeting.Status = MeetingStatus.InProgress;
                meeting.RoomActivatedAtUtc ??= DateTime.UtcNow;
                meeting.RaiseDomainEvent(new MeetingStartedEvent(orgId, meeting.Id));

                await _dbContext.SaveChangesAsync(cancellationToken);
                return Result.Success(await LoadMeetingResponseAsync(meeting.Id, cancellationToken));
            }

            if (meeting.Status == MeetingStatus.InProgress)
            {
                if (meeting.RoomActivatedAtUtc == null)
                {
                    meeting.RoomActivatedAtUtc = DateTime.UtcNow;
                    await _dbContext.SaveChangesAsync(cancellationToken);
                }

                return Result.Success(await LoadMeetingResponseAsync(meeting.Id, cancellationToken));
            }

            return Result.Failure<MeetingResponse>(MeetingErrors.InvalidLifecycleTransition);
        }

        public async Task<Result<MeetingResponse>> EndMeetingAsync(
            Guid meetingId,
            Guid userId,
            CancellationToken cancellationToken = default)
        {
            var orgId = GetOrganizationId();

            var meeting = await _dbContext.Meetings
                .Include(m => m.Participants)
                .FirstOrDefaultAsync(m => m.Id == meetingId, cancellationToken);

            if (meeting == null)
                return Result.Failure<MeetingResponse>(MeetingErrors.NotFound);

            var authorizationResult = await EnsureLifecycleControlAllowedAsync(meeting, orgId, userId, cancellationToken);
            if (authorizationResult.IsFailure)
                return Result.Failure<MeetingResponse>(authorizationResult.Error);

            if (meeting.Status == MeetingStatus.InProgress)
            {
                meeting.Status = MeetingStatus.Completed;
                meeting.RaiseDomainEvent(new MeetingEndedEvent(orgId, meeting.Id));

                await _dbContext.SaveChangesAsync(cancellationToken);
                return Result.Success(await LoadMeetingResponseAsync(meeting.Id, cancellationToken));
            }

            if (meeting.Status == MeetingStatus.Completed)
            {
                return Result.Success(await LoadMeetingResponseAsync(meeting.Id, cancellationToken));
            }

            return Result.Failure<MeetingResponse>(MeetingErrors.InvalidLifecycleTransition);
        }

        private async Task<Result> ValidateAndAssociateTagsAsync(
            Meeting meeting,
            List<Guid> tagIds,
            Guid orgId,
            CancellationToken cancellationToken)
        {
            var activeTags = await _dbContext.MeetingTags
                .IgnoreQueryFilters()
                .Where(t => tagIds.Contains(t.Id) && t.OrganizationId == orgId && t.IsActive)
                .Select(t => t.Id)
                .ToListAsync(cancellationToken);

            if (activeTags.Count != tagIds.Count)
                return Result.Failure(MeetingErrors.TagNotFound);

            foreach (var tagId in tagIds)
            {
                meeting.Tags.Add(new MeetingMeetingTag
                {
                    MeetingId = meeting.Id,
                    MeetingTagId = tagId
                });
            }

            return Result.Success();
        }

        private async Task<MeetingResponse> LoadMeetingResponseAsync(Guid meetingId, CancellationToken cancellationToken)
        {
            var meeting = await _dbContext.Meetings
                .Include(m => m.Participants).ThenInclude(p => p.User)
                .Include(m => m.Tags)
                .FirstAsync(m => m.Id == meetingId, cancellationToken);

            return meeting.Adapt<MeetingResponse>();
        }

        private async Task<Result> EnsureLifecycleControlAllowedAsync(
            Meeting meeting,
            Guid orgId,
            Guid userId,
            CancellationToken cancellationToken)
        {
            var membershipRole = await _dbContext.UserOrgMemberships
                .IgnoreQueryFilters()
                .Where(m => m.UserId == userId && m.OrganizationId == orgId && m.IsEnabled)
                .Select(m => (OrganizationRole?)m.OrgRole)
                .FirstOrDefaultAsync(cancellationToken);

            if (membershipRole is null)
                return Result.Failure(MeetingErrors.NotOrgMember);

            if (membershipRole == OrganizationRole.Admin)
                return Result.Success();

            var callerParticipant = meeting.Participants.FirstOrDefault(p => p.UserId == userId);
            if (callerParticipant is { MeetingRole: MeetingRole.Host or MeetingRole.CoHost })
                return Result.Success();

            return Result.Failure(MeetingErrors.LifecycleControlForbidden);
        }

        private Guid GetOrganizationId()
        {
            return _tenantProvider.CurrentOrganizationId
                ?? throw new UnauthorizedAccessException("No active organization context found.");
        }
    }
}
