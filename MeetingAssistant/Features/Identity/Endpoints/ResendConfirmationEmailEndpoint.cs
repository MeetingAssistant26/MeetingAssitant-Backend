using Microsoft.AspNetCore.Mvc;
using MeetingAssistant.Shared.Abstractions;
using MeetingAssistant.Features.Identity.DTOs;

namespace MeetingAssistant.Features.Identity.Endpoints
{
    public partial class AuthController
    {
        [HttpPost("resend-confirmation-email")]
        public async Task<IActionResult> ResendConfirmationEmail([FromBody] ResendConfirmationEmailRequest resendconfirmEmailRequest, CancellationToken cancellationToken)
        {
            var result = await _authService.ResendConfirmationEmailAsync(resendconfirmEmailRequest, cancellationToken);

            return result.IsSuccess ? Ok()
                            : result.ToProblem(_correlationIdProvider);
        }
    }
}
