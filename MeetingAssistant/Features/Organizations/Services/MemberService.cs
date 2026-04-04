using MeetingAssistant.Features.Organizations.Contracts.Requests;
using MeetingAssistant.Features.Organizations.Contracts.Responses;
using MeetingAssistant.Features.Organizations.Models;
using MeetingAssistant.Features.Organizations.Models.Events;
using MeetingAssistant.Infrastructure.Persistence.DbContext;
using MeetingAssistant.Shared.Abstractions;
using MeetingAssistant.Shared.Errors;
using Microsoft.EntityFrameworkCore;

namespace MeetingAssistant.Features.Organizations.Services
{
    public class MemberService(ApplicationDbContext dbContext) : IMemberService
    {
        private readonly ApplicationDbContext _dbContext = dbContext;

        public async Task<Result<IEnumerable<MemberResponse>>> ListMembersAsync(
            Guid organizationId,
            CancellationToken cancellationToken = default)
        {
            var organizationExists = await _dbContext.Organizations
                .AnyAsync(o => o.Id == organizationId, cancellationToken);
                
            if (!organizationExists)
                return Result.Failure<IEnumerable<MemberResponse>>(OrganizationErrors.NotFound);

            var members = await _dbContext.UserOrgMemberships
                .Where(m => m.OrganizationId == organizationId && m.IsEnabled)
                .Select(m => new MemberResponse(
                    m.UserId,
                    m.User.Email!,
                    m.User.DisplayName ?? string.Empty,
                    m.OrgRole.ToString(),
                    m.JobRole,
                    m.Context,
                    m.ContextStatus.HasValue ? m.ContextStatus.ToString() : null,
                    m.IsEnabled))
                .ToListAsync(cancellationToken);

            return Result.Success<IEnumerable<MemberResponse>>(members);
        }

        public async Task<Result> UpdateMemberRoleAsync(
            Guid organizationId,
            Guid userId,
            UpdateMemberRoleRequest request,
            CancellationToken cancellationToken = default)
        {
            var membership = await _dbContext.UserOrgMemberships
                .FirstOrDefaultAsync(m => m.OrganizationId == organizationId && m.UserId == userId && m.IsEnabled, cancellationToken);

            if (membership is null)
                return Result.Failure(OrganizationErrors.MemberNotFound);

            var newRole = Enum.Parse<OrganizationRole>(request.OrgRole, ignoreCase: true);

            if (membership.OrgRole == newRole)
                return Result.Success(); // No change

            // Invariant Check: Cannot demote the last admin
            if (membership.OrgRole == OrganizationRole.Admin && newRole != OrganizationRole.Admin)
            {
                var adminCount = await _dbContext.UserOrgMemberships
                    .CountAsync(m => m.OrganizationId == organizationId && m.OrgRole == OrganizationRole.Admin && m.IsEnabled, cancellationToken);

                if (adminCount <= 1)
                    return Result.Failure(OrganizationErrors.LastAdmin);
            }

            var oldRole = membership.OrgRole;
            membership.OrgRole = newRole;

            membership.RaiseDomainEvent(new RoleChangedEvent(organizationId, userId, oldRole, newRole));

            await _dbContext.SaveChangesAsync(cancellationToken);

            return Result.Success();
        }

        public async Task<Result> UpdateMemberContextAsync(
            Guid organizationId,
            Guid memberUserId,
            UpdateMemberContextRequest request,
            Guid currentUserId,
            CancellationToken cancellationToken = default)
        {
            var membership = await _dbContext.UserOrgMemberships
                .FirstOrDefaultAsync(m => m.OrganizationId == organizationId && m.UserId == memberUserId && m.IsEnabled, cancellationToken);

            if (membership is null)
                return Result.Failure(OrganizationErrors.MemberNotFound);

            var currMembership = await _dbContext.UserOrgMemberships
                .FirstOrDefaultAsync(m => m.OrganizationId == organizationId && m.UserId == currentUserId && m.IsEnabled, cancellationToken);
            
            if (currMembership is null)
                return Result.Failure(OrganizationErrors.Unauthorized);

            if (currentUserId != memberUserId && currMembership.OrgRole != OrganizationRole.Admin)
                return Result.Failure(OrganizationErrors.Unauthorized);

            membership.JobRole = request.JobRole;
            membership.Context = request.Context;
            membership.ContextStatus = ContextStatus.Pending;

            membership.RaiseDomainEvent(new MemberContextUpdatedEvent(organizationId, memberUserId));

            await _dbContext.SaveChangesAsync(cancellationToken);

            return Result.Success();
        }
    }
}