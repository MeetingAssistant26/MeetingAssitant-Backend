using Mapster;
using MeetingAssistant.Api.Infrastructure.Services;
using MeetingAssistant.Features.Meetings.Contracts.Requests;
using MeetingAssistant.Features.Meetings.Contracts.Responses;
using MeetingAssistant.Features.Meetings.Models;
using MeetingAssistant.Features.Meetings.Models.Events;
using MeetingAssistant.Features.Organizations.Models;
using MeetingAssistant.Infrastructure.Persistence.DbContext;
using MeetingAssistant.Shared.Abstractions;
using MeetingAssistant.Shared.Errors;
using Microsoft.EntityFrameworkCore;
namespace MeetingAssistant.Features.Meetings.Services
{
    public class MeetingService(
        ApplicationDbContext dbContext,
        ITenantProvider tenantProvider) : IMeetingService
    {
        private readonly ApplicationDbContext _dbContext = dbContext;
        private readonly ITenantProvider _tenantProvider = tenantProvider;

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

            var meeting = new Meeting
            {
                OrganizationId = orgId,
                Title = request.Title,
                Description = request.Description,
                ScheduledStartUtc = request.ScheduledStartUtc,
                ScheduledEndUtc = request.ScheduledEndUtc,
                Status = MeetingStatus.Scheduled
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

        private Guid GetOrganizationId()
        {
            return _tenantProvider.CurrentOrganizationId
                ?? throw new UnauthorizedAccessException("No active organization context found.");
        }
    }
}
