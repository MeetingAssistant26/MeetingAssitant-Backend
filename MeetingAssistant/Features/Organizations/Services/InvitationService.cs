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
    }
}