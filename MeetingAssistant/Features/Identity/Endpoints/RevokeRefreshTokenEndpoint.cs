using Microsoft.AspNetCore.Mvc;
using MeetingAssistant.Shared.Abstractions;
using MeetingAssistant.Features.Identity.DTOs;

namespace MeetingAssistant.Features.Identity.Endpoints
{
    public partial class AuthController
    {
        [HttpPost("revoke-refresh-token")]
        public async Task<IActionResult> RevokeRefresh(RefreshTokenRequest refreshTokenRequest, CancellationToken cancellationToken)
        {
            var IsRevoked = await _authService.RevokeRefreshTokenAsync(refreshTokenRequest.Token, refreshTokenRequest.RefreshToken, cancellationToken);

            return IsRevoked.IsSuccess ? Ok()
                            : IsRevoked.ToProblem();
        }
    }
}
