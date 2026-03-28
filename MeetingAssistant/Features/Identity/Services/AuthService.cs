using Hangfire;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.UI.Services;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using MeetingAssistant.Shared.Abstractions.Consts;
using System.Security.Cryptography;
using System.Text;
using static Org.BouncyCastle.Crypto.Engines.SM2Engine;
using Mapster;
using MeetingAssistant.Shared.Abstractions;
using MeetingAssistant.Features.Identity.DTOs;
using MeetingAssistant.Features.Identity.Entites;
using MeetingAssistant.Features.Identity.Models.Responses;
using MeetingAssistant.Shared.Errors;
using MeetingAssistant.Shared.Helpers;
using MeetingAssistant.Infrastructure.Persistence.DbContext;

namespace MeetingAssistant.Features.Identity.Services
{
    public class AuthService(UserManager<ApplicationUser> userManager,
        SignInManager<ApplicationUser> signInManager,
        ILogger<AuthService> logger,
        ITokenService tokenService,
        IEmailSender emailSender,
        IHttpContextAccessor httpContextAccessor,
        ApplicationDbContext Context,
        MediatR.IPublisher publisher
        ) : IAuthService
    {
        private readonly UserManager<ApplicationUser> _userManager = userManager;
        private readonly SignInManager<ApplicationUser> _signInManager = signInManager;
        private readonly ILogger _logger = logger;
        private readonly ITokenService _tokenService = tokenService;
        private readonly IEmailSender _emailSender = emailSender;
        private readonly IHttpContextAccessor _httpContextAccessor = httpContextAccessor;
        private readonly ApplicationDbContext _context = Context;
        private readonly MediatR.IPublisher _publisher = publisher;
        private readonly int _refreshTokenExpiryDays=14;

        public async Task<Result<AuthResponse>> GetTokenAsync(string email, string password, CancellationToken cancellationToken)
        {
            var user = await _userManager.FindByEmailAsync(email);
            if (user == null)
                return Result.Failure<AuthResponse>(UserErrors.InvalidCredentials );

            var result = await _signInManager.PasswordSignInAsync(user, password, false, false);
            if (result.Succeeded)
            {
                var (userRoles, userPermissions) = await GetUserRolesAndPermissions(user, cancellationToken);


                var (token, expiresIn) = _tokenService.GenerateAccessToken(user,userRoles, userPermissions);

                var refreshToken = _tokenService.GenerateRefreshToken();
                var refreshTokenHash = _tokenService.HashToken(refreshToken);
                var refreshTokenExpiry = DateTime.UtcNow.AddDays(_refreshTokenExpiryDays);

                user.RefreshTokens.Add(new RefreshToken
                {
                    TokenHash = refreshTokenHash,
                    ExpiresAtUtc = refreshTokenExpiry,
                    CreatedAtUtc = DateTime.UtcNow,


                });
                await _userManager.UpdateAsync(user);

                var response = new AuthResponse(user.Id.ToString(), user.DisplayName ?? string.Empty, user.Email!, user.ProfileAvatarUrl, token, expiresIn, refreshToken, refreshTokenExpiry);

                return Result.Success(response);
            }


            return Result.Failure<AuthResponse>(result.IsNotAllowed? UserErrors.EmailNotConfirmed :  UserErrors.InvalidCredentials);

           
        }



        public async Task<Result<AuthResponse>> GetRefreshTokenAsync(string token, string refreshToken, CancellationToken cancellationToken)
        {
            var userID= _tokenService.ValidateToken(token);
            if (userID == null)
                return Result.Failure<AuthResponse>(UserErrors.InvalidToken);
            var user = await _userManager.FindByIdAsync(userID);
            if (user == null)
                return Result.Failure<AuthResponse>(UserErrors.UserNotFound);
            var userRefreshToken = user.RefreshTokens.FirstOrDefault(rt => rt.TokenHash == _tokenService.HashToken(refreshToken)&& rt.IsActive);
            if (userRefreshToken == null)
                return Result.Failure<AuthResponse>(UserErrors.InvalidRefreshToken);

            userRefreshToken.RevokedAtUtc = DateTime.UtcNow;

            var (userRoles, userPermissions) = await GetUserRolesAndPermissions(user, cancellationToken);

            var (newtoken, expiresIn) = _tokenService.GenerateAccessToken(user, userRoles, userPermissions);

            var newrefreshToken = _tokenService.GenerateRefreshToken();
            var newrefreshTokenHash = _tokenService.HashToken(newrefreshToken);
            var refreshTokenExpiry = DateTime.UtcNow.AddDays(_refreshTokenExpiryDays);

            user.RefreshTokens.Add(new RefreshToken
            {
                TokenHash = newrefreshTokenHash,
                ExpiresAtUtc = refreshTokenExpiry,
                CreatedAtUtc = DateTime.UtcNow,


            });
            await _userManager.UpdateAsync(user);

            var response= new AuthResponse(user.Id.ToString(), user.DisplayName ?? string.Empty, user.Email!, user.ProfileAvatarUrl, newtoken, expiresIn, newrefreshToken, refreshTokenExpiry);
            return Result.Success(response);
        }

        

        public async Task<Result> RevokeRefreshTokenAsync(string token, string refreshToken, CancellationToken cancellationToken)
        {
            var userID = _tokenService.ValidateToken(token);
            if (userID == null)
                return Result.Failure(UserErrors.InvalidToken);
            var user = await _userManager.FindByIdAsync(userID);
            if (user == null)
                return Result.Failure(UserErrors.UserNotFound);
            var userRefreshToken = user.RefreshTokens.FirstOrDefault(rt => rt.TokenHash == _tokenService.HashToken(refreshToken) && rt.IsActive);
            if (userRefreshToken == null)
                return Result.Failure(UserErrors.InvalidRefreshToken);

            userRefreshToken.RevokedAtUtc = DateTime.UtcNow;

            await _userManager.UpdateAsync(user);
            return Result.Success();

        }

        public async Task<Result<RegisterResponse>> RegisterAsync(RegisterRequest request,CancellationToken cancellationToken)
        {
            var emailIsExist= await _userManager.Users.AnyAsync(u=>u.Email == request.Email,cancellationToken);
            if (emailIsExist)
                return Result.Failure<RegisterResponse>(UserErrors.DuplicatedEmail);

            var user = request.Adapt<ApplicationUser>();
            user.UserName = request.Email;
            user.DisplayName = request.DisplayName;
            user.CreatedAtUtc = DateTime.UtcNow;

            var result=await _userManager.CreateAsync(user,request.Password);
            if (result.Succeeded)
            {
                var code=await _userManager.GenerateEmailConfirmationTokenAsync(user);
                code=WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(code));
                _logger.LogInformation("confirmation code: {code}, UserId: {UserId}", code, user.Id);
                await SendConfirmationEmail(user, code);

                await _publisher.Publish(new Events.UserRegisteredEvent(user.Id, user.Email!), cancellationToken);

                return Result.Success(new RegisterResponse(user.Id, user.Email!, user.DisplayName));
            }

            var error = result.Errors.First();
            return Result.Failure<RegisterResponse>(new Error(error.Code, error.Description, StatusCodes.Status400BadRequest));
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
                return Result.Failure(UserErrors.DuplicatedConfirmation);

            var code = await _userManager.GenerateEmailConfirmationTokenAsync(user);
            code = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(code));

            _logger.LogInformation("confirmation code: {code}, UserId: {UserId}", code,user.Id);

           await  SendConfirmationEmail(user, code);
            return Result.Success();



        }

        public async Task<Result> SendResetPasswordCodeAsync(ForgetPasswordRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var user = await _userManager.FindByEmailAsync(request.Email);
            if (user == null)
                return Result.Success();

           if(!user.EmailConfirmed)
            return Result.Failure(UserErrors.EmailNotConfirmed);


            var code = await _userManager.GeneratePasswordResetTokenAsync(user);
            code = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(code));

            _logger.LogInformation("Reset code: {code}, UserId: {UserId}", code, user.Id);

            await SendResetPasswordEmail(user, code);
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
                return Result.Success();

            var error = result.Errors.First();

            return Result.Failure(new Error(error.Code, error.Description, StatusCodes.Status401Unauthorized));
        }

        private static string GenerateRefreshToken()
        {
            return Convert.ToBase64String(RandomNumberGenerator.GetBytes(64));
        }

        private async Task SendConfirmationEmail(ApplicationUser user,string code)
        {
            var origin = _httpContextAccessor.HttpContext!.Request.Headers.Origin;
            var emailBody = EmailBodyBuilder.GenerateEmailBody("EmailConfirmation",

                templateValues: new Dictionary<string, string>
                {
                        {"{{name}}",user.DisplayName!},
                        {"{{action_url}}",$"{origin}/auth/emailConfirmation?userId={user.Id}&code={code}" }
                }
            );

            BackgroundJob.Enqueue<IEmailSender>(x=>x.SendEmailAsync(user.Email!, "Meeting Assistant :Email Confirmation", emailBody));
            await Task.CompletedTask;

        }

        private async Task SendResetPasswordEmail(ApplicationUser user, string code)
        {
            var origin = _httpContextAccessor.HttpContext!.Request.Headers.Origin;
            var emailBody = EmailBodyBuilder.GenerateEmailBody("ForgetPassword",

                templateValues: new Dictionary<string, string>
                {
                        {"{{name}}",user.DisplayName!},
                        {"{{action_url}}",$"{origin}/auth/forgetPassword?userId={user.Id}&code={code}" }
                }
            );

            BackgroundJob.Enqueue(() => _emailSender.SendEmailAsync(user.Email!, "Survey Basket :Change Password", emailBody));

            await Task.CompletedTask;

        }

        private async Task<(IEnumerable<string> roles, IEnumerable<string> permissions)> GetUserRolesAndPermissions(ApplicationUser user, CancellationToken cancellationToken)
        {
            var userRoles = await _userManager.GetRolesAsync(user);

          
            var userPermissions = await (from r in _context.Roles
                                         join p in _context.RoleClaims
                                         on r.Id equals p.RoleId
                                         where userRoles.Contains(r.Name!)
                                         select p.ClaimValue!)
                                         .Distinct()
                                         .ToListAsync(cancellationToken);

            return (userRoles, userPermissions);
        }


    }
    
}




