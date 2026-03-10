using Microsoft.AspNetCore.Mvc;
using MeetingAssistant.Shared.Abstractions;
using MeetingAssistant.Features.Identity.DTOs;

namespace MeetingAssistant.Features.Identity.Endpoints
{
    public partial class AuthController
    {
        [HttpPost("forget-password")]
        public async Task<IActionResult> ForgetPassword([FromBody] ForgetPasswordRequest resetPasswordRequest)
        {
            var result = await _authService.SendResetPasswordCodeAsync(resetPasswordRequest);

            return result.IsSuccess ? Ok()
                            : result.ToProblem();
        }
    }
}
