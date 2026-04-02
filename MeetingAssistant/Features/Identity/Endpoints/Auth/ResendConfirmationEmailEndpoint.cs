using Microsoft.AspNetCore.Mvc;
using MeetingAssistant.Shared.Abstractions;
using MeetingAssistant.Features.Identity.DTOs;

using Microsoft.AspNetCore.Authorization;
namespace MeetingAssistant.Features.Identity.Endpoints
{
    public partial class AuthController
    {
        [AllowAnonymous]
        [HttpPost("resend-confirmation-email")]
        public async Task<IActionResult> ResendConfirmationEmail([FromBody] ResendConfirmationEmailRequest resendconfirmEmailRequest, CancellationToken cancellationToken)
        {
            var result = await _authService.ResendConfirmationEmailAsync(resendconfirmEmailRequest, cancellationToken);

            return result.IsSuccess ? Ok()
                            : result.ToProblem(_correlationIdProvider);
        }
    }
}
