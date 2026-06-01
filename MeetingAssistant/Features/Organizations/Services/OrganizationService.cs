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
    public class OrganizationService(
        ApplicationDbContext dbContext,
        ISlugGenerator slugGenerator) : IOrganizationService
    {
        private readonly ApplicationDbContext _dbContext = dbContext;
        private readonly ISlugGenerator _slugGenerator = slugGenerator;

        public async Task<Result<OrganizationResponse>> CreateOrganizationAsync(
            CreateOrganizationRequest request,
            Guid userId,
            CancellationToken cancellationToken = default)
        {
            var hasActiveMembership = await _dbContext.UserOrgMemberships
                .IgnoreQueryFilters()
                .AnyAsync(m => m.UserId == userId && m.IsEnabled, cancellationToken);

            if (hasActiveMembership)
                return Result.Failure<OrganizationResponse>(OrganizationErrors.AlreadyHasMembership);

            var slug = await _slugGenerator.GenerateUniqueSlugAsync(request.Name, cancellationToken);

            var organization = new Organization
            {
                Name = request.Name,
                Slug = slug
            };

            var membership = new UserOrgMembership
            {
                UserId = userId,
                OrganizationId = organization.Id,
                OrgRole = OrganizationRole.Admin,
                IsEnabled = true
            };

            organization.RaiseDomainEvent(new OrganizationCreatedEvent(organization.Id, userId));
            organization.RaiseDomainEvent(new MemberJoinedEvent(organization.Id, userId, OrganizationRole.Admin));

            _dbContext.Organizations.Add(organization);
            _dbContext.UserOrgMemberships.Add(membership);
            await _dbContext.SaveChangesAsync(cancellationToken);

            return Result.Success(ToResponse(organization, membership.OrgRole));
        }

        public async Task<Result<OrganizationListResponse>> ListOrganizationsAsync(
            Guid userId,
            CancellationToken cancellationToken = default)
        {
            var memberships = await _dbContext.UserOrgMemberships
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(m => m.UserId == userId && m.IsEnabled)
                .Include(m => m.Organization)
                .OrderBy(m => m.Organization.Name)
                .ThenBy(m => m.OrganizationId)
                .ToListAsync(cancellationToken);

            var items = memberships
                .Select(m => ToResponse(m.Organization, m.OrgRole))
                .ToList();

            return Result.Success(new OrganizationListResponse(items));
        }

        public async Task<Result<OrganizationResponse>> GetOrganizationAsync(
            Guid organizationId,
            Guid userId,
            CancellationToken cancellationToken = default)
        {
            var organizationExists = await _dbContext.Organizations
                .AsNoTracking()
                .AnyAsync(o => o.Id == organizationId, cancellationToken);

            if (!organizationExists)
                return Result.Failure<OrganizationResponse>(OrganizationErrors.NotFound);

            var membership = await _dbContext.UserOrgMemberships
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Include(m => m.Organization)
                .FirstOrDefaultAsync(
                    m => m.OrganizationId == organizationId && m.UserId == userId && m.IsEnabled,
                    cancellationToken);

            if (membership is null)
                return Result.Failure<OrganizationResponse>(OrganizationErrors.Unauthorized);

            return Result.Success(ToResponse(membership.Organization, membership.OrgRole));
        }

        public async Task<Result> LeaveOrganizationAsync(
            Guid organizationId,
            Guid userId,
            CancellationToken cancellationToken = default)
        {
            var membership = await _dbContext.UserOrgMemberships
                .FirstOrDefaultAsync(m => m.OrganizationId == organizationId && m.UserId == userId && m.IsEnabled, cancellationToken);

            if (membership is null)
                return Result.Failure(OrganizationErrors.MemberNotFound);

            if (membership.OrgRole == OrganizationRole.Admin)
            {
                var adminCount = await _dbContext.UserOrgMemberships
                    .CountAsync(m => m.OrganizationId == organizationId && m.OrgRole == OrganizationRole.Admin && m.IsEnabled, cancellationToken);

                if (adminCount <= 1)
                    return Result.Failure(OrganizationErrors.LastAdmin);
            }

            membership.IsEnabled = false;

            // Revoke all refresh tokens so the user can't get new JWTs with the old org claims
            await _dbContext.RefreshTokens
                .Where(rt => rt.UserId == userId && rt.RevokedAtUtc == null)
                .ExecuteUpdateAsync(s => s.SetProperty(rt => rt.RevokedAtUtc, DateTime.UtcNow), cancellationToken);

            membership.RaiseDomainEvent(new MemberLeftEvent(organizationId, userId));

            await _dbContext.SaveChangesAsync(cancellationToken);

            return Result.Success();
        }

        private static OrganizationResponse ToResponse(Organization organization, OrganizationRole role) => new(
            organization.Id,
            organization.Name,
            organization.Slug,
            role.ToString(),
            organization.CreatedAtUtc,
            organization.UpdatedAtUtc);
    }
}
