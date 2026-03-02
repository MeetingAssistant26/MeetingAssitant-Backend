using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using MeetingAssistant.Services;
using MeetingAssistant.Abstractions;
using MeetingAssistant.Contracts.Authentication;

namespace MeetingAssistant.Controllers
{
    [Route("/[controller]")]
    [ApiController]
    public class AuthController(IAuthService authService) : ControllerBase
    {
        private readonly IAuthService _authService = authService;

        [HttpPost]
        public async Task<IActionResult> Login(Contracts.Authentication.LoginRequest loginRequest,CancellationToken cancellationToken)
        {
            var authresult =await _authService.GetTokenAsync(loginRequest.Email,loginRequest.Password, cancellationToken);

            return authresult.IsSuccess ? Ok(authresult.Value)
                           : authresult.ToProblem();

        }

        [HttpPost("refresh")]
        public async Task<IActionResult> Refresh(RefreshTokenRequest refreshTokenRequest, CancellationToken cancellationToken)
        {
            var authresult = await _authService.GetRefreshTokenAsync(refreshTokenRequest.Token,refreshTokenRequest.RefreshToken, cancellationToken);

            return authresult.IsSuccess ? Ok(authresult.Value)
                  :authresult.ToProblem();
        }
        [HttpPost("revoke-refresh-token")]
        public async Task<IActionResult> RevokeRefresh(RefreshTokenRequest refreshTokenRequest, CancellationToken cancellationToken)
        {
            var IsRevoked = await _authService.RevokeRefreshTokenAsync(refreshTokenRequest.Token, refreshTokenRequest.RefreshToken, cancellationToken);

            return IsRevoked.IsSuccess ? Ok()
                            : IsRevoked.ToProblem();
        }
        [HttpPost("register")]
        public async Task<IActionResult> Register([FromBody]RegisterRequest registerRequest, CancellationToken cancellationToken)
        {
            if (!ModelState.IsValid)
            {
                return BadRequest(ModelState);
            }
            var result= await _authService.RegisterAsync(registerRequest,cancellationToken);

            return result.IsSuccess ? Ok()
                            : result.ToProblem();
        }

        [HttpPost("confirm-email")]
        public async Task<IActionResult> ConfirmEmail([FromBody] ConfirmEmailRequest confirmEmailRequest, CancellationToken cancellationToken)
        {
            var result = await _authService.ConfirmEmailAsync(confirmEmailRequest, cancellationToken);

            return result.IsSuccess ? Ok()
                            : result.ToProblem();
        }
        [HttpPost("resend-confirmation-email")]
        public async Task<IActionResult> ResendConfirmationEmail([FromBody] ResendConfirmationEmailRequest resendconfirmEmailRequest, CancellationToken cancellationToken)
        {
            var result = await _authService.ResendConfirmationEmailAsync(resendconfirmEmailRequest, cancellationToken);

            return result.IsSuccess ? Ok()
                            : result.ToProblem();
        }

        [HttpPost("forget-password")]
        public async Task<IActionResult> ForgetPassword([FromBody] ForgetPasswordRequest resetPasswordRequest)
        {
            var result = await _authService.SendResetPasswordCodeAsync(resetPasswordRequest);

            return result.IsSuccess ? Ok()
                            : result.ToProblem();
        }

        [HttpPost("reset-password")]
        public async Task<IActionResult> ResetPassword([FromBody] ResetPasswordRequest request)
        {
            var result = await _authService.ResetPasswordAsync(request);

            return result.IsSuccess ? Ok() : result.ToProblem();
        }
    }
}
