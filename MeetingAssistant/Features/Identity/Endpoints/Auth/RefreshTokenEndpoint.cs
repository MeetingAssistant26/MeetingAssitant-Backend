using Microsoft.AspNetCore.Mvc;
using MeetingAssistant.Shared.Abstractions;
using MeetingAssistant.Features.Identity.Models.Requests;
using Microsoft.AspNetCore.Authorization;

namespace MeetingAssistant.Features.Identity.Endpoints
{
    public partial class AuthController
    {
        [AllowAnonymous]
        [HttpPost("refresh")]
        public async Task<IActionResult> Refresh([FromBody] RefreshTokenRequest refreshTokenRequest, CancellationToken cancellationToken)
        {
            var authresult = await _authService.RefreshAsync(refreshTokenRequest, cancellationToken);

            return authresult.IsSuccess ? Ok(authresult.Value)
                  : authresult.ToProblem(_correlationIdProvider);
        }
    }
}
