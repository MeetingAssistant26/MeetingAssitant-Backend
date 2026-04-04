using System.Security.Cryptography;
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
    public class InvitationService(ApplicationDbContext dbContext) : IInvitationService
    {
        private readonly ApplicationDbContext _dbContext = dbContext;

        public async Task<Result<CreateInvitationResponse>> CreateInvitationAsync(
            Guid organizationId,
            Guid userId,
            CreateInvitationRequest request,
            CancellationToken cancellationToken = default)
        {
            var tokenBytes = new byte[32];
            using var rng = RandomNumberGenerator.Create();
            rng.GetBytes(tokenBytes);
            var token = Convert.ToBase64String(tokenBytes)
                .Replace("+", "-")
                .Replace("/", "_")
                .TrimEnd('=');

            var invitation = new Invitation
            {
                OrganizationId = organizationId,
                InvitedByUserId = userId,
                Token = token,
                EmailWhitelist = request.EmailWhitelist,
                ExpiresAtUtc = DateTime.UtcNow.AddDays(7)
            };

            _dbContext.Invitations.Add(invitation);
            await _dbContext.SaveChangesAsync(cancellationToken);

            var response = new CreateInvitationResponse(
                invitation.Id,
                invitation.Token,
                invitation.ExpiresAtUtc
            );

            return Result.Success(response);
        }

        public async Task<Result> RevokeInvitationAsync(
            Guid organizationId,
            Guid userId,
            Guid invitationId,
            CancellationToken cancellationToken = default)
        {
            var invitation = await _dbContext.Invitations
                .FirstOrDefaultAsync(i => i.Id == invitationId && i.OrganizationId == organizationId, cancellationToken);

            if (invitation is null)
                return Result.Failure(OrganizationErrors.InvitationNotFound);

            if (invitation.RevokedAtUtc.HasValue)
                return Result.Failure(OrganizationErrors.InvitationRevoked);

            if (invitation.ExpiresAtUtc < DateTime.UtcNow)
                return Result.Failure(OrganizationErrors.InvitationExpired);

            invitation.RevokedAtUtc = DateTime.UtcNow;
            invitation.RevokedByUserId = userId;
            
            invitation.RaiseDomainEvent(new InvitationRevokedEvent(organizationId, invitationId));

            await _dbContext.SaveChangesAsync(cancellationToken);

            return Result.Success();
        }

        public async Task<Result<MemberResponse>> JoinInvitationAsync(
            string token,
            Guid userId,
            CancellationToken cancellationToken = default)
        {
            var invitation = await _dbContext.Invitations
                .IgnoreQueryFilters()
                .Include(i => i.Organization)
                .FirstOrDefaultAsync(i => i.Token == token, cancellationToken);

            if (invitation is null)
                return Result.Failure<MemberResponse>(OrganizationErrors.InvitationNotFound);

            if (invitation.RevokedAtUtc.HasValue)
                return Result.Failure<MemberResponse>(OrganizationErrors.InvitationRevoked);

            if (invitation.ExpiresAtUtc < DateTime.UtcNow)
                return Result.Failure<MemberResponse>(OrganizationErrors.InvitationExpired);

            var user = await _dbContext.Users.FindAsync(new object[] { userId }, cancellationToken);
            if (user is null)
                return Result.Failure<MemberResponse>(OrganizationErrors.Unauthorized);

            if (invitation.EmailWhitelist.Count != 0 && !invitation.EmailWhitelist.Contains(user.Email!, StringComparer.OrdinalIgnoreCase))
                return Result.Failure<MemberResponse>(OrganizationErrors.EmailNotWhitelisted);

            // Verify no active org membership
            var hasActiveMembership = await _dbContext.UserOrgMemberships
                .IgnoreQueryFilters()
                .AnyAsync(m => m.UserId == userId && m.IsEnabled, cancellationToken);

            if (hasActiveMembership)
                return Result.Failure<MemberResponse>(OrganizationErrors.AlreadyHasMembership);

            var membership = new UserOrgMembership
            {
                UserId = userId,
                OrganizationId = invitation.OrganizationId,
                OrgRole = OrganizationRole.Member,
                IsEnabled = true
            };

            membership.RaiseDomainEvent(new MemberJoinedEvent(invitation.OrganizationId, userId, OrganizationRole.Member));

            _dbContext.UserOrgMemberships.Add(membership);
            await _dbContext.SaveChangesAsync(cancellationToken);

            var response = new MemberResponse(
                userId,
                user.Email!,
                user.DisplayName ?? string.Empty,
                OrganizationRole.Member.ToString(),
                null,
                null,
                null,
                true
            );

            return Result.Success(response);
        }
    }
}