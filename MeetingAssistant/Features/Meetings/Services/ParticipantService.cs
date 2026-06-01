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
        ITenantProvider tenantProvider,
        IMeetingConflictService meetingConflictService) : IParticipantService
    {
        private readonly ApplicationDbContext _dbContext = dbContext;
        private readonly ITenantProvider _tenantProvider = tenantProvider;
        private readonly IMeetingConflictService _meetingConflictService = meetingConflictService;

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
                    AlreadyParticipant = m.Participants.Any(p => p.UserId == request.UserId),
                    m.ScheduledStartUtc,
                    m.ScheduledEndUtc
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

            var conflicts = await _meetingConflictService.FindConflictsAsync(
                orgId,
                [request.UserId],
                check.ScheduledStartUtc,
                check.ScheduledEndUtc,
                meetingId,
                cancellationToken);

            if (conflicts.Count > 0)
                return Result.Failure<ParticipantResponse>(MeetingErrors.ConflictDetectedWithDetails(conflicts));
            
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

        public async Task<Result<ParticipantResponse>> UpdateParticipantRoleAsync(
            Guid meetingId,
            Guid userIdToUpdate,
            UpdateParticipantRoleRequest request,
            Guid callerId,
            CancellationToken cancellationToken = default)
        {
            var orgId = GetOrganizationId();

            if (!request.MeetingRole.HasValue)
                return Result.Failure<ParticipantResponse>(MeetingErrors.InvalidParticipantRole);

            var requestedRole = request.MeetingRole.Value;

            var data = await _dbContext.Meetings
                .Where(m => m.Id == meetingId && m.OrganizationId == orgId)
                .Select(m => new
                {
                    CallerRole = m.Participants
                        .Where(p => p.UserId == callerId)
                        .Select(p => (MeetingRole?)p.MeetingRole)
                        .FirstOrDefault(),
                    TargetId = m.Participants
                        .Where(p => p.UserId == userIdToUpdate)
                        .Select(p => (Guid?)p.Id)
                        .FirstOrDefault(),
                    TargetRole = m.Participants
                        .Where(p => p.UserId == userIdToUpdate)
                        .Select(p => (MeetingRole?)p.MeetingRole)
                        .FirstOrDefault(),
                    HostCount = m.Participants.Count(p => p.MeetingRole == MeetingRole.Host)
                })
                .FirstOrDefaultAsync(cancellationToken);

            if (data == null)
                return Result.Failure<ParticipantResponse>(MeetingErrors.NotFound);

            if (data.CallerRole == null)
                return Result.Failure<ParticipantResponse>(MeetingErrors.NotParticipant);

            if (data.CallerRole != MeetingRole.Host && data.CallerRole != MeetingRole.CoHost)
                return Result.Failure<ParticipantResponse>(MeetingErrors.NotHost);

            if (data.TargetId == null || data.TargetRole == null)
                return Result.Failure<ParticipantResponse>(MeetingErrors.ParticipantNotFound);

            var targetIsActiveOrgMember = await _dbContext.UserOrgMemberships
                .IgnoreQueryFilters()
                .AnyAsync(
                    m => m.UserId == userIdToUpdate
                         && m.OrganizationId == orgId
                         && m.IsEnabled,
                    cancellationToken);

            if (!targetIsActiveOrgMember)
                return Result.Failure<ParticipantResponse>(MeetingErrors.NotOrgMember);

            if (data.CallerRole == MeetingRole.CoHost
                && (data.TargetRole is MeetingRole.Host or MeetingRole.CoHost
                    || requestedRole is MeetingRole.Host or MeetingRole.CoHost))
            {
                return Result.Failure<ParticipantResponse>(MeetingErrors.RoleUpdateForbidden);
            }

            if (data.TargetRole == MeetingRole.Host
                && requestedRole != MeetingRole.Host
                && data.HostCount <= 1)
            {
                return Result.Failure<ParticipantResponse>(MeetingErrors.LastHostRoleChange);
            }

            var participant = await _dbContext.MeetingParticipants
                .Include(p => p.User)
                .FirstAsync(p => p.Id == data.TargetId.Value, cancellationToken);

            participant.MeetingRole = requestedRole;

            await _dbContext.SaveChangesAsync(cancellationToken);

            return Result.Success(participant.Adapt<ParticipantResponse>());
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
            var conflicts = await _meetingConflictService.FindConflictsAsync(
                orgId,
                userIds,
                meetingData.ScheduledStartUtc,
                meetingData.ScheduledEndUtc,
                meetingId,
                cancellationToken);

            return Result.Success(conflicts.ToList());
        }
    }
}
