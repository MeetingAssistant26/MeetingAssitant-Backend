using Microsoft.AspNetCore.Mvc;
using MeetingAssistant.Shared.Abstractions;
using MeetingAssistant.Features.Identity.DTOs;

using Microsoft.AspNetCore.Authorization;
namespace MeetingAssistant.Features.Identity.Endpoints
{
    public partial class AuthController
    {
        [AllowAnonymous]
        [HttpPost("reset-password")]
        public async Task<IActionResult> ResetPassword([FromBody] ResetPasswordRequest request, CancellationToken cancellationToken)
        {
            var result = await _authService.ResetPasswordAsync(request, cancellationToken);

            return result.IsSuccess ? Ok() : result.ToProblem(_correlationIdProvider);
        }
    }
}
