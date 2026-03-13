using Microsoft.AspNetCore.Mvc;
using MeetingAssistant.Shared.Abstractions;
using MeetingAssistant.Features.Identity.DTOs;

namespace MeetingAssistant.Features.Identity.Endpoints
{
    public partial class AuthController
    {
        [HttpPost("confirm-email")]
        public async Task<IActionResult> ConfirmEmail([FromBody] ConfirmEmailRequest confirmEmailRequest, CancellationToken cancellationToken)
        {
            var result = await _authService.ConfirmEmailAsync(confirmEmailRequest, cancellationToken);

            return result.IsSuccess ? Ok()
                            : result.ToProblem(_correlationIdProvider);
        }
    }
}
