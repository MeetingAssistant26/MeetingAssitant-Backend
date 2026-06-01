using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using MediatR;
using MeetingAssistant.Shared.Abstractions;
using MeetingAssistant.Features.Identity.Entites;
using MeetingAssistant.Features.Identity.Models.Responses;
using MeetingAssistant.Features.Identity.Models.Requests;
using MeetingAssistant.Features.Identity.Events;
using MeetingAssistant.Shared.Errors;
using MeetingAssistant.Infrastructure.Persistence.DbContext;

namespace MeetingAssistant.Features.Identity.Services
{
    public class ProfileService(
        UserManager<ApplicationUser> userManager,
        ApplicationDbContext dbContext,
        IPublisher publisher) : IProfileService
    {
        private readonly UserManager<ApplicationUser> _userManager = userManager;
        private readonly ApplicationDbContext _dbContext = dbContext;
        private readonly IPublisher _publisher = publisher;

        public async Task<Result<UserProfileResponse>> GetProfileAsync(Guid userId, CancellationToken cancellationToken)
        {
            var profile = await _userManager.Users
                .AsNoTracking()
                .Where(u => u.Id == userId)
                .Select(u => new UserProfileResponse(
                    u.Id,
                    u.Email!,
                    u.DisplayName ?? string.Empty,
                    u.ProfileAvatarUrl))
                .FirstOrDefaultAsync(cancellationToken);

            if (profile == null)
                return Result.Failure<UserProfileResponse>(UserErrors.UserNotFound);

            return Result.Success(profile);
        }

        public async Task<Result<UserProfileResponse>> UpdateProfileAsync(Guid userId, UpdateProfileRequest request, CancellationToken cancellationToken)
        {
            var user = await _userManager.FindByIdAsync(userId.ToString());
            if (user == null)
            {
                return Result.Failure<UserProfileResponse>(UserErrors.UserNotFound);
            }

            user.DisplayName = request.DisplayName;
            if (request.ProfileAvatarUrlWasProvided)
            {
                user.ProfileAvatarUrl = string.IsNullOrWhiteSpace(request.ProfileAvatarUrl)
                    ? null
                    : request.ProfileAvatarUrl.Trim();
            }
            user.UpdatedAtUtc = DateTime.UtcNow;

            await _userManager.UpdateAsync(user);

            return Result.Success(new UserProfileResponse(
                user.Id,
                user.Email!,
                user.DisplayName ?? string.Empty,
                user.ProfileAvatarUrl
            ));
        }

        public async Task<Result> ChangePasswordAsync(Guid userId, ChangePasswordRequest request, CancellationToken cancellationToken)
        {
            var user = await _userManager.FindByIdAsync(userId.ToString());
            if (user == null)
            {
                return Result.Failure(UserErrors.UserNotFound);
            }

            var changeResult = await _userManager.ChangePasswordAsync(user, request.CurrentPassword, request.NewPassword);
            if (!changeResult.Succeeded)
            {
                var error = changeResult.Errors.First();
                return Result.Failure(new Error(error.Code, error.Description, StatusCodes.Status400BadRequest));
            }

            await _dbContext.RefreshTokens
                .Where(rt => rt.UserId == userId && rt.RevokedAtUtc == null)
                .ExecuteUpdateAsync(s => s.SetProperty(rt => rt.RevokedAtUtc, DateTime.UtcNow), cancellationToken);

            await _publisher.Publish(new PasswordChangedEvent(user.Id), cancellationToken);

            return Result.Success();
        }
    }
}
