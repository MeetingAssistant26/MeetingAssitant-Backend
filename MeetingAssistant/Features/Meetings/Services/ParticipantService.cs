using Mapster;
using MeetingAssistant.Api.Infrastructure.Services;
using MeetingAssistant.Features.Meetings.Contracts.Requests;
using MeetingAssistant.Features.Meetings.Contracts.Responses;
using MeetingAssistant.Features.Meetings.Models;
using MeetingAssistant.Infrastructure.Persistence.DbContext;
using MeetingAssistant.Shared.Abstractions;
using MeetingAssistant.Shared.Errors;
using Microsoft.EntityFrameworkCore;

namespace MeetingAssistant.Features.Meetings.Services
{
    public class ParticipantService(
        ApplicationDbContext dbContext,
        ITenantProvider tenantProvider) : IParticipantService
    {
        private readonly ApplicationDbContext _dbContext = dbContext;
        private readonly ITenantProvider _tenantProvider = tenantProvider;

        private Guid GetOrganizationId()
        {
            var orgId = _tenantProvider.CurrentOrganizationId ?? Guid.Empty;
            if (orgId == Guid.Empty)
                throw new UnauthorizedAccessException("Organization context is missing.");
            return orgId;
        }

        public async Task<Result<ParticipantResponse>> AddParticipantAsync(
            Guid meetingId,
            AddParticipantRequest request,
            Guid callerId,
            CancellationToken cancellationToken = default)
        {
            var orgId = GetOrganizationId();

            var check = await _dbContext.Meetings
                .Where(m => m.Id == meetingId)
                .Select(m => new
                {
                    CallerRole = m.Participants
                        .Where(p => p.UserId == callerId)
                        .Select(p => (MeetingRole?)p.MeetingRole)
                        .FirstOrDefault(),
                    AlreadyParticipant = m.Participants.Any(p => p.UserId == request.UserId)
                })
                .FirstOrDefaultAsync(cancellationToken);

            if (check == null)
                return Result.Failure<ParticipantResponse>(MeetingErrors.NotFound);

            if (check.CallerRole == null)
                return Result.Failure<ParticipantResponse>(MeetingErrors.NotParticipant);

            if (check.CallerRole != MeetingRole.Host && check.CallerRole != MeetingRole.CoHost)
                return Result.Failure<ParticipantResponse>(MeetingErrors.NotHost);

            var targetMembership = await _dbContext.UserOrgMemberships
                .IgnoreQueryFilters()
                .Include(m => m.User)
                .FirstOrDefaultAsync(m => m.UserId == request.UserId && m.OrganizationId == orgId && m.IsEnabled, cancellationToken);

            if (targetMembership == null)
                return Result.Failure<ParticipantResponse>(MeetingErrors.NotOrgMember);

            if (check.AlreadyParticipant)
                return Result.Failure<ParticipantResponse>(MeetingErrors.AlreadyParticipant);

            var newParticipant = new MeetingParticipant
            {
                MeetingId = meetingId,
                OrganizationId = orgId,
                UserId = request.UserId,
                User = targetMembership.User,
                MeetingRole = request.MeetingRole
            };

            _dbContext.MeetingParticipants.Add(newParticipant);
            await _dbContext.SaveChangesAsync(cancellationToken);

            return Result.Success(newParticipant.Adapt<ParticipantResponse>());
        }

        public async Task<Result> RemoveParticipantAsync(
            Guid meetingId,
            Guid userIdToRemove,
            Guid callerId,
            CancellationToken cancellationToken = default)
        {
            var data = await _dbContext.Meetings
                .Where(m => m.Id == meetingId)
                .Select(m => new
                {
                    CallerRole = m.Participants
                        .Where(p => p.UserId == callerId)
                        .Select(p => (MeetingRole?)p.MeetingRole)
                        .FirstOrDefault(),
                    TargetId = m.Participants
                        .Where(p => p.UserId == userIdToRemove)
                        .Select(p => (Guid?)p.Id)
                        .FirstOrDefault(),
                    TargetRole = m.Participants
                        .Where(p => p.UserId == userIdToRemove)
                        .Select(p => (MeetingRole?)p.MeetingRole)
                        .FirstOrDefault(),
                    HostCount = m.Participants.Count(p => p.MeetingRole == MeetingRole.Host)
                })
                .FirstOrDefaultAsync(cancellationToken);

            if (data == null)
                return Result.Failure(MeetingErrors.NotFound);

            if (data.CallerRole == null)
                return Result.Failure(MeetingErrors.NotParticipant);

            if (data.CallerRole != MeetingRole.Host && data.CallerRole != MeetingRole.CoHost)
                return Result.Failure(MeetingErrors.NotHost);

            if (data.TargetId == null)
                return Result.Failure(MeetingErrors.ParticipantNotFound);

            if (data.CallerRole == MeetingRole.CoHost)
            {
                if (data.TargetRole == MeetingRole.Host || data.TargetRole == MeetingRole.CoHost)
                {
                    return Result.Failure(MeetingErrors.RemovalForbidden);
                }
            }

            if (data.TargetRole == MeetingRole.Host && data.HostCount <= 1)
            {
                return Result.Failure(MeetingErrors.LastHost);
            }

            var stub = new MeetingParticipant { Id = data.TargetId.Value };
            _dbContext.MeetingParticipants.Attach(stub);
            _dbContext.MeetingParticipants.Remove(stub);
            await _dbContext.SaveChangesAsync(cancellationToken);

            return Result.Success();
        }

        public async Task<Result<List<ConflictResponse>>> CheckConflictsAsync(
            Guid meetingId,
            Guid callerId,
            CancellationToken cancellationToken = default)
        {
            var orgId = GetOrganizationId();

            var meetingData = await _dbContext.Meetings
                .Where(m => m.Id == meetingId && m.OrganizationId == orgId)
                .Select(m => new
                {
                    CallerRole = m.Participants
                        .Where(p => p.UserId == callerId)
                        .Select(p => (MeetingRole?)p.MeetingRole)
                        .FirstOrDefault(),
                    m.ScheduledStartUtc,
                    m.ScheduledEndUtc,
                    Participants = m.Participants.Select(p => new
                    {
                        p.UserId,
                        DisplayName = p.User != null ? p.User.DisplayName ?? string.Empty : string.Empty
                    }).ToList()
                })
                .FirstOrDefaultAsync(cancellationToken);

            if (meetingData == null)
                return Result.Failure<List<ConflictResponse>>(MeetingErrors.NotFound);

            if (meetingData.CallerRole == null)
                return Result.Failure<List<ConflictResponse>>(MeetingErrors.NotParticipant);

            if (meetingData.CallerRole != MeetingRole.Host && meetingData.CallerRole != MeetingRole.CoHost)
                return Result.Failure<List<ConflictResponse>>(MeetingErrors.NotHost);

            var userIds = meetingData.Participants.Select(p => p.UserId).ToList();

            var otherMeetings = await _dbContext.Meetings
                .Where(m => m.OrganizationId == orgId && m.Id != meetingId)
                .Where(m => m.Status != MeetingStatus.Cancelled && m.Status != MeetingStatus.Completed && m.Status != MeetingStatus.Failed)
                .Where(m => m.ScheduledStartUtc < meetingData.ScheduledEndUtc && m.ScheduledEndUtc > meetingData.ScheduledStartUtc)
                .Where(m => m.Participants.Any(p => userIds.Contains(p.UserId)))
                .ProjectToType<MeetingResponse>()
                .ToListAsync(cancellationToken);

            var conflicts = new List<ConflictResponse>();

            foreach (var participant in meetingData.Participants)
            {
                var userConflicts = otherMeetings
                    .Where(m => m.Participants.Any(p => p.UserId == participant.UserId))
                    .ToList();

                if (userConflicts.Count > 0)
                {
                    conflicts.Add(new ConflictResponse(participant.UserId, participant.DisplayName, userConflicts));
                }
            }

            return Result.Success(conflicts);
        }
    }
}
