using Hangfire;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.UI.Services;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using MeetingAssistant.Abstractions;
using MeetingAssistant.Authentication;
using MeetingAssistant.Contracts.Authentication;
using MeetingAssistant.Errors;
using MeetingAssistant.Abstractions.Consts;
using MeetingAssistant.Contracts.Authentication;
using MeetingAssistant.Helpers;
using System.Security.Cryptography;
using System.Text;
using static Org.BouncyCastle.Crypto.Engines.SM2Engine;
using MeetingAssistant.Entities;
using MeetingAssistant.Persistence;
using Mapster;

namespace MeetingAssistant.Services
{
    public class AuthService(UserManager<ApplicationUser> userManager,
        SignInManager<ApplicationUser> signInManager,
        ILogger<AuthService> logger,
        IJwtProvider jwtProvider,
        IEmailSender emailSender,
        IHttpContextAccessor httpContextAccessor,
        ApplicationDbContext Context
        ) : IAuthService
    {
        private readonly UserManager<ApplicationUser> _userManager = userManager;
        private readonly SignInManager<ApplicationUser> _signInManager = signInManager;
        private readonly ILogger _logger = logger;
        private readonly IJwtProvider _jwtProvider = jwtProvider;
        private readonly IEmailSender _emailSender = emailSender;
        private readonly IHttpContextAccessor _httpContextAccessor = httpContextAccessor;
        private readonly ApplicationDbContext _context = Context;
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


                var (token, expiresIn) = _jwtProvider.GenerateJwtToken(user,userRoles, userPermissions);

                var refreshToken = GenerateRefreshToken();
                var refreshTokenExpiry = DateTime.UtcNow.AddDays(_refreshTokenExpiryDays);

                user.RefreshTokens.Add(new RefreshToken
                {
                    Token = refreshToken,
                    ExpiresOn = refreshTokenExpiry,
                    CreatedOn = DateTime.UtcNow,


                });
                await _userManager.UpdateAsync(user);

                var response = new AuthResponse(user.Id, user.FirstName, user.LastName, user.Email,user.ProfileAvatarUrl, token, expiresIn, refreshToken, refreshTokenExpiry);

                return Result.Success<AuthResponse>(response);
            }


            return Result.Failure<AuthResponse>(result.IsNotAllowed? UserErrors.EmailNotConfirmed :  UserErrors.InvalidCredentials);

           
        }



        public async Task<Result<AuthResponse>> GetRefreshTokenAsync(string token, string refreshToken, CancellationToken cancellationToken)
        {
            var userID= _jwtProvider.validateJwtToken(token);
            if (userID == null)
                return Result.Failure<AuthResponse>(UserErrors.InvalidToken);
            var user = await _userManager.FindByIdAsync(userID);
            if (user == null)
                return Result.Failure<AuthResponse>(UserErrors.UserNotFound);
            var userRefreshToken = user.RefreshTokens.FirstOrDefault(rt => rt.Token == refreshToken&& rt.IsActive);
            if (userRefreshToken == null)
                return Result.Failure<AuthResponse>(UserErrors.InvalidRefreshToken);

            userRefreshToken.RevokedOn = DateTime.UtcNow;

            var (userRoles, userPermissions) = await GetUserRolesAndPermissions(user, cancellationToken);

            var (newtoken, expiresIn) = _jwtProvider.GenerateJwtToken(user, userRoles, userPermissions);

            var newrefreshToken = GenerateRefreshToken();
            var refreshTokenExpiry = DateTime.UtcNow.AddDays(_refreshTokenExpiryDays);

            user.RefreshTokens.Add(new RefreshToken
            {
                Token = newrefreshToken,
                ExpiresOn = refreshTokenExpiry,
                CreatedOn = DateTime.UtcNow,


            });
            await _userManager.UpdateAsync(user);

            var response= new AuthResponse(user.Id, user.FirstName, user.LastName, user.Email, user.ProfileAvatarUrl, newtoken, expiresIn, newrefreshToken, refreshTokenExpiry);
            return Result.Success<AuthResponse>(response);
        }

        

        public async Task<Result> RevokeRefreshTokenAsync(string token, string refreshToken, CancellationToken cancellationToken)
        {
            var userID = _jwtProvider.validateJwtToken(token);
            if (userID == null)
                return Result.Failure(UserErrors.InvalidToken);
            var user = await _userManager.FindByIdAsync(userID);
            if (user == null)
                return Result.Failure(UserErrors.UserNotFound);
            var userRefreshToken = user.RefreshTokens.FirstOrDefault(rt => rt.Token == refreshToken && rt.IsActive);
            if (userRefreshToken == null)
                return Result.Failure<AuthResponse>(UserErrors.InvalidRefreshToken);

            userRefreshToken.RevokedOn = DateTime.UtcNow;

            await _userManager.UpdateAsync(user);
            return Result.Success();

        }

        public async Task<Result> RegisterAsync(RegisterRequest request,CancellationToken cancellationToken)
        {
            var emailIsExist= await _userManager.Users.AnyAsync(u=>u.Email == request.Email,cancellationToken);
            if (emailIsExist)
                return Result.Failure(UserErrors.DuplicatedEmail);

            var user = request.Adapt<ApplicationUser>();

            var result=await _userManager.CreateAsync(user,request.Password);
            if (result.Succeeded)
            {
                var code=await _userManager.GenerateEmailConfirmationTokenAsync(user);
                code=WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(code));
                _logger.LogInformation("confirmation code:{code},UserId{user.Id}", code, user.Id);
                await SendConfirmationEmail(user, code);

                return Result.Success();
            }

            var error = result.Errors.First();
            return Result.Failure(new Error(error.Code, error.Description, StatusCodes.Status400BadRequest));

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

            _logger.LogInformation("confirmation code:{code},UserId{user.Id}", code,user.Id);

           await  SendConfirmationEmail(user, code);
            return Result.Success();



        }

        public async Task<Result> SendResetPasswordCodeAsync(ForgetPasswordRequest request)
        {
            var user = await _userManager.FindByEmailAsync(request.Email);
            if (user == null)
                return Result.Success();

           if(!user.EmailConfirmed)
            return Result.Failure(UserErrors.EmailNotConfirmed);


            var code = await _userManager.GeneratePasswordResetTokenAsync(user);
            code = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(code));

            _logger.LogInformation("Reset code:{code},UserId{user.Id}", code, user.Id);

            await SendResetPasswordEmail(user, code);
            return Result.Success();
        }

        public async Task<Result> ResetPasswordAsync(ResetPasswordRequest request)
        {
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
                        {"{{name}}",user.FirstName!},
                        {"{{action_url}}",$"{origin}/auth/emailConfirmation?userId={user.Id}&code={code}" }
                }
            );

            BackgroundJob.Enqueue(() => _emailSender.SendEmailAsync(user.Email!, "Survey Basket :Email Confirmation", emailBody));

            await Task.CompletedTask;

        }

        private async Task SendResetPasswordEmail(ApplicationUser user, string code)
        {
            var origin = _httpContextAccessor.HttpContext!.Request.Headers.Origin;
            var emailBody = EmailBodyBuilder.GenerateEmailBody("ForgetPassword",

                templateValues: new Dictionary<string, string>
                {
                        {"{{name}}",user.FirstName!},
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
