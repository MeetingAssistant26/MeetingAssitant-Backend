using Hangfire;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.UI.Services;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Mapster;
using System.Text;
using MeetingAssistant.Shared.Abstractions;
using MeetingAssistant.Features.Identity.Models.Requests;
using MeetingAssistant.Features.Identity.DTOs;
using MeetingAssistant.Features.Identity.Entites;
using MeetingAssistant.Features.Identity.Models.Responses;
using MeetingAssistant.Features.Organizations.Models;
using MeetingAssistant.Shared.Errors;
using MeetingAssistant.Shared.Helpers;
using MeetingAssistant.Infrastructure.Persistence.DbContext;
using MeetingAssistant.Api.Infrastructure.Configuration;
using Microsoft.Extensions.Options;

namespace MeetingAssistant.Features.Identity.Services
{
    public class AuthService(UserManager<ApplicationUser> userManager,
        SignInManager<ApplicationUser> signInManager,
        ILogger<AuthService> logger,
        ITokenService tokenService,
        IEmailSender emailSender,
        IConfiguration configuration,
        IOptions<JwtSettings> jwtSettingsOptions,
        ApplicationDbContext Context,
        MediatR.IPublisher publisher,
        IBackgroundJobClient backgroundJobClient
        ) : IAuthService
    {
        private readonly UserManager<ApplicationUser> _userManager = userManager;
        private readonly SignInManager<ApplicationUser> _signInManager = signInManager;
        private readonly ILogger _logger = logger;
        private readonly ITokenService _tokenService = tokenService;
        private readonly IEmailSender _emailSender = emailSender;
        private readonly string _appUrl = configuration["AppUrl"] ?? throw new InvalidOperationException("AppUrl is not configured.");
        private readonly ApplicationDbContext _context = Context;
        private readonly MediatR.IPublisher _publisher = publisher;
        private readonly int _refreshTokenExpiryDays = jwtSettingsOptions.Value.RefreshTokenExpiryDays;
        private readonly IBackgroundJobClient _backgroundJobClient = backgroundJobClient;

        public async Task<Result<AuthTokenResponse>> LoginAsync(LoginRequest request, CancellationToken cancellationToken)
        {
            var user = await _userManager.FindByEmailAsync(request.Email);
            if (user == null)
                return Result.Failure<AuthTokenResponse>(UserErrors.InvalidCredentials);

            if (!user.EmailConfirmed)
                return Result.Failure<AuthTokenResponse>(UserErrors.EmailNotConfirmed);

            var result = await _signInManager.CheckPasswordSignInAsync(user, request.Password, lockoutOnFailure: true);
            if (result.IsLockedOut)
            {
                _logger.LogWarning("Login attempt for locked account. UserId: {UserId}", user.Id);
                return Result.Failure<AuthTokenResponse>(UserErrors.AccountLocked);
            }
            if (!result.Succeeded)
            {
                _logger.LogWarning("Failed login attempt. UserId: {UserId}", user.Id);
                return Result.Failure<AuthTokenResponse>(UserErrors.InvalidCredentials);
            }

            var membership = await GetActiveMembership(user.Id, cancellationToken);

            var (token, expiresIn) = _tokenService.GenerateAccessToken(user,
                membership?.OrganizationId, membership?.OrgRole.ToString());

            var refreshToken = _tokenService.GenerateRefreshToken();
            var refreshTokenHash = _tokenService.HashToken(refreshToken);
            var refreshTokenExpiry = DateTime.UtcNow.AddDays(_refreshTokenExpiryDays);

            var refreshTokenEntity = new RefreshToken
            {
                TokenHash = refreshTokenHash,
                UserId = user.Id,
                FamilyId = Guid.NewGuid().ToString(),
                ExpiresAtUtc = refreshTokenExpiry,
                CreatedAtUtc = DateTime.UtcNow,
            };
            _context.RefreshTokens.Add(refreshTokenEntity);
            await _context.SaveChangesAsync(cancellationToken);


            _logger.LogInformation("User logged in successfully. UserId: {UserId}", user.Id);
            await _publisher.Publish(new Events.UserLoggedInEvent(user.Id), cancellationToken);

            var response = new AuthTokenResponse(token, refreshToken, refreshTokenExpiry, expiresIn);
            return Result.Success(response);
        }



        public async Task<Result<AuthTokenResponse>> RefreshAsync(RefreshTokenRequest request, CancellationToken cancellationToken)
        {
            var tokenHash = _tokenService.HashToken(request.RefreshToken);

            // Query RefreshTokens directly instead of loading all user tokens
            var userRefreshToken = await _context.RefreshTokens
                .FirstOrDefaultAsync(rt => rt.TokenHash == tokenHash, cancellationToken);

            if (userRefreshToken == null)
                return Result.Failure<AuthTokenResponse>(UserErrors.InvalidRefreshToken);

            if (userRefreshToken.IsRevoked)
            {
                if (userRefreshToken.GracePeriodExpiresAtUtc > DateTime.UtcNow
                    && userRefreshToken.ReplacedByTokenHash != null)
                {
                    // Grace period: return the already-issued replacement token
                    var existingReplacement = await _context.RefreshTokens
                        .AsNoTracking()
                        .FirstOrDefaultAsync(rt => rt.TokenHash == userRefreshToken.ReplacedByTokenHash, cancellationToken);

                    if (existingReplacement != null)
                    {
                        var graceUser = await _userManager.FindByIdAsync(userRefreshToken.UserId.ToString());
                        if (graceUser == null)
                            return Result.Failure<AuthTokenResponse>(UserErrors.InvalidRefreshToken);

                        var graceMembership = await GetActiveMembership(graceUser.Id, cancellationToken);
                        var (graceAccessToken, graceExpiresIn) = _tokenService.GenerateAccessToken(graceUser,
                            graceMembership?.OrganizationId, graceMembership?.OrgRole.ToString());

                        // Return the existing replacement refresh token (raw token can't be recovered — client should use the one from the first response)
                        // We return a new access token but signal the same refresh token expiry
                        return Result.Success(new AuthTokenResponse(graceAccessToken, request.RefreshToken, existingReplacement.ExpiresAtUtc, graceExpiresIn));
                    }
                }

                // COMPROMISED: batch-revoke all tokens in family
                await _context.RefreshTokens
                    .Where(rt => rt.FamilyId == userRefreshToken.FamilyId && rt.RevokedAtUtc == null)
                    .ExecuteUpdateAsync(s => s.SetProperty(rt => rt.RevokedAtUtc, DateTime.UtcNow), cancellationToken);

                await _publisher.Publish(new Events.RefreshTokenCompromiseDetectedEvent(userRefreshToken.UserId, userRefreshToken.FamilyId), cancellationToken);
                return Result.Failure<AuthTokenResponse>(UserErrors.InvalidRefreshToken);
            }

            if (userRefreshToken.IsExpired)
                return Result.Failure<AuthTokenResponse>(UserErrors.InvalidRefreshToken);

            var newrefreshToken = _tokenService.GenerateRefreshToken();
            var newrefreshTokenHash = _tokenService.HashToken(newrefreshToken);
            var refreshTokenExpiry = DateTime.UtcNow.AddDays(_refreshTokenExpiryDays);

            // Revoke old token with grace period
            userRefreshToken.RevokedAtUtc = DateTime.UtcNow;
            userRefreshToken.GracePeriodExpiresAtUtc = DateTime.UtcNow.AddSeconds(30);
            userRefreshToken.ReplacedByTokenHash = newrefreshTokenHash;

            _context.RefreshTokens.Add(new RefreshToken
            {
                TokenHash = newrefreshTokenHash,
                UserId = userRefreshToken.UserId,
                FamilyId = userRefreshToken.FamilyId,
                ExpiresAtUtc = refreshTokenExpiry,
                CreatedAtUtc = DateTime.UtcNow,
            });
            await _context.SaveChangesAsync(cancellationToken);

            // Load user only after validation passes
            var user = await _userManager.FindByIdAsync(userRefreshToken.UserId.ToString());
            if (user == null)
                return Result.Failure<AuthTokenResponse>(UserErrors.InvalidRefreshToken);

            if (!user.EmailConfirmed)
                return Result.Failure<AuthTokenResponse>(UserErrors.EmailNotConfirmed);

            if (user.LockoutEnd > DateTime.UtcNow)
                return Result.Failure<AuthTokenResponse>(UserErrors.AccountLocked);

            var membership = await GetActiveMembership(user.Id, cancellationToken);
            var (newtoken, expiresIn) = _tokenService.GenerateAccessToken(user,
                membership?.OrganizationId, membership?.OrgRole.ToString());

            _logger.LogInformation("User token refreshed. UserId: {UserId}, FamilyId: {FamilyId}", user.Id, userRefreshToken.FamilyId);
            await _publisher.Publish(new Events.TokenRefreshedEvent(user.Id, userRefreshToken.FamilyId), cancellationToken);

            var response = new AuthTokenResponse(newtoken, newrefreshToken, refreshTokenExpiry, expiresIn);
            return Result.Success(response);
        }

        

        public async Task<Result> LogoutAsync(LogoutRequest request, CancellationToken cancellationToken)
        {
            var tokenHash = _tokenService.HashToken(request.RefreshToken);

            var userRefreshToken = await _context.RefreshTokens
                .FirstOrDefaultAsync(rt => rt.TokenHash == tokenHash, cancellationToken);

            if (userRefreshToken == null || !userRefreshToken.IsActive)
                return Result.Success();

            await _context.RefreshTokens
                .Where(rt => rt.FamilyId == userRefreshToken.FamilyId && rt.RevokedAtUtc == null)
                .ExecuteUpdateAsync(s => s.SetProperty(rt => rt.RevokedAtUtc, DateTime.UtcNow), cancellationToken);

            _logger.LogInformation("User logged out successfully. UserId: {UserId}", userRefreshToken.UserId);
            await _publisher.Publish(new Events.UserLoggedOutEvent(userRefreshToken.UserId), cancellationToken);

            return Result.Success();
        }

        public async Task<Result<RegisterResponse>> RegisterAsync(RegisterRequest request,CancellationToken cancellationToken)
        {
            var normalizedEmail = _userManager.NormalizeEmail(request.Email);
            var emailIsExist = await _userManager.Users.AnyAsync(u => u.NormalizedEmail == normalizedEmail, cancellationToken);
            if (emailIsExist)
                return Result.Failure<RegisterResponse>(UserErrors.DuplicatedEmail);

            var user = request.Adapt<ApplicationUser>();
            user.UserName = request.Email;
            user.DisplayName = request.DisplayName;
            user.CreatedAtUtc = DateTime.UtcNow;

            var result = await _userManager.CreateAsync(user, request.Password);
            if (result.Succeeded)
            {
                var code = await _userManager.GenerateEmailConfirmationTokenAsync(user);
                code = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(code));
                _logger.LogInformation("confirmation code: {code}, UserId: {UserId}", code, user.Id);
                SendConfirmationEmail(user, code);


                _logger.LogInformation("User registered successfully. UserId: {UserId}, Email: {Email}", user.Id, user.Email);
                await _publisher.Publish(new Events.UserRegisteredEvent(user.Id, user.Email!), cancellationToken);

                return Result.Success(new RegisterResponse(user.Id, user.Email!, user.DisplayName));
            }

            return Result.Failure<RegisterResponse>(MapIdentityErrors(result.Errors));
        }

        private static Error MapIdentityErrors(IEnumerable<IdentityError> errors)
        {
            var groupedErrors = errors
                .GroupBy(e => e.Code is "DuplicateUserName" or "DuplicateEmail"
                    ? "Email"
                    : e.Code.StartsWith("Password")
                        ? "Password"
                        : "General")
                .ToDictionary(
                    g => g.Key,
                    g => g.Select(e => e.Code is "DuplicateUserName" or "DuplicateEmail"
                        ? UserErrors.DuplicatedEmail.Description
                        : e.Description)
                    .Distinct().ToArray());

            return new Error("Identity.ValidationError", "One or more identity validation errors occurred.", StatusCodes.Status400BadRequest)
            {
                Errors = groupedErrors
            };
        }

        public async Task<Result> ConfirmEmailAsync(ConfirmEmailRequest request, CancellationToken cancellationToken)
        {
            var user = await _userManager.FindByIdAsync(request.UserId);

            if(user==null)
                return Result.Failure(UserErrors.InvalidCode);

            if(user.EmailConfirmed)
                return Result.Failure(UserErrors.DuplicatedConfirmation);

            var code = request.Code;
            try
            {
                code = Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(code));
            }
            catch (FormatException) 
            { 
                return Result.Failure(UserErrors.InvalidCode);
            }
            var result = await _userManager.ConfirmEmailAsync(user, code);
            if (result.Succeeded)
            {     
                return Result.Success();
            }
                



            var error = result.Errors.First();
            return Result.Failure(new Error(error.Code, error.Description, StatusCodes.Status400BadRequest));

        }

        public async Task<Result> ResendConfirmationEmailAsync(ResendConfirmationEmailRequest request, CancellationToken cancellationToken)
        {
            var user = await _userManager.FindByEmailAsync(request.Email);
            if (user == null)
                return Result.Success();
            if (user.EmailConfirmed)
                return Result.Success();

            var code = await _userManager.GenerateEmailConfirmationTokenAsync(user);
            code = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(code));

            _logger.LogInformation("confirmation code: {code}, UserId: {UserId}", code, user.Id);

            SendConfirmationEmail(user, code);
            return Result.Success();



        }

        public async Task<Result> SendResetPasswordCodeAsync(ForgetPasswordRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var user = await _userManager.FindByEmailAsync(request.Email);
            if (user == null)
                return Result.Success();

            if (!user.EmailConfirmed)
                return Result.Success();


            var code = await _userManager.GeneratePasswordResetTokenAsync(user);
            code = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(code));

            _logger.LogInformation("Reset code: {code}, UserId: {UserId}", code, user.Id);

            SendResetPasswordEmail(user, code);
            return Result.Success();
        }

        public async Task<Result> ResetPasswordAsync(ResetPasswordRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var user = await _userManager.FindByEmailAsync(request.Email);

            if (user is null || !user.EmailConfirmed)
                return Result.Failure(UserErrors.InvalidCode);

            IdentityResult result;

            try
            {
                var code = Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(request.Code));
                result = await _userManager.ResetPasswordAsync(user, code, request.NewPassword);
            }
            catch (FormatException)
            {
                result = IdentityResult.Failed(_userManager.ErrorDescriber.InvalidToken());
            }

            if (result.Succeeded)
            {
                // Revoke all active refresh tokens — account was likely compromised
                await _context.RefreshTokens
                    .Where(rt => rt.UserId == user.Id && rt.RevokedAtUtc == null)
                    .ExecuteUpdateAsync(s => s.SetProperty(rt => rt.RevokedAtUtc, DateTime.UtcNow), cancellationToken);

                return Result.Success();
            }

            var error = result.Errors.First();

            return Result.Failure(new Error(error.Code, error.Description, StatusCodes.Status401Unauthorized));
        }

        private void SendConfirmationEmail(ApplicationUser user, string code)
        {
            var emailBody = EmailBodyBuilder.GenerateEmailBody("EmailConfirmation",
                templateValues: new Dictionary<string, string>
                {
                    {"{{name}}", user.DisplayName!},
                    {"{{action_url}}", $"{_appUrl}/auth/emailConfirmation?userId={user.Id}&code={code}"}
                }
            );

            _backgroundJobClient.Enqueue<IEmailSender>(x => x.SendEmailAsync(user.Email!, "Meeting Assistant: Email Confirmation", emailBody));
        }

        private void SendResetPasswordEmail(ApplicationUser user, string code)
        {
            var emailBody = EmailBodyBuilder.GenerateEmailBody("ForgetPassword",
                templateValues: new Dictionary<string, string>
                {
                    {"{{name}}", user.DisplayName!},
                    {"{{action_url}}", $"{_appUrl}/auth/forgetPassword?userId={user.Id}&code={code}"}
                }
            );

            _backgroundJobClient.Enqueue<IEmailSender>(x => x.SendEmailAsync(user.Email!, "Meeting Assistant: Reset Password", emailBody));
        }

        private async Task<UserOrgMembership?> GetActiveMembership(Guid userId, CancellationToken cancellationToken)
        {
            return await _context.UserOrgMemberships
                .IgnoreQueryFilters()
                .AsNoTracking()
                .FirstOrDefaultAsync(m => m.UserId == userId && m.IsEnabled, cancellationToken);
        }

    }
    
}





