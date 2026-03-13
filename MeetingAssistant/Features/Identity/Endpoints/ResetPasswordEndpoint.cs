using Microsoft.AspNetCore.Mvc;
using MeetingAssistant.Shared.Abstractions;
using MeetingAssistant.Features.Identity.DTOs;

namespace MeetingAssistant.Features.Identity.Endpoints
{
    public partial class AuthController
    {
        [HttpPost("reset-password")]
        public async Task<IActionResult> ResetPassword([FromBody] ResetPasswordRequest request, CancellationToken cancellationToken)
        {
            var result = await _authService.ResetPasswordAsync(request, cancellationToken);

            return result.IsSuccess ? Ok() : result.ToProblem(_correlationIdProvider);
        }
    }
}
