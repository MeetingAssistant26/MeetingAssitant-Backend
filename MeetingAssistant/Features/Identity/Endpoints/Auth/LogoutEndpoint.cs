using Microsoft.AspNetCore.Mvc;
using MeetingAssistant.Shared.Abstractions;
using MeetingAssistant.Features.Identity.Models.Requests;
using Microsoft.AspNetCore.Authorization;

namespace MeetingAssistant.Features.Identity.Endpoints
{
    public partial class AuthController
    {
        [Authorize]
        [HttpPost("logout")]
        public async Task<IActionResult> Logout([FromBody] LogoutRequest logoutRequest, CancellationToken cancellationToken)
        {
            var IsRevoked = await _authService.LogoutAsync(logoutRequest, cancellationToken);

            return IsRevoked.IsSuccess ? Ok()
                            : IsRevoked.ToProblem(_correlationIdProvider);      
        }
    }
}
