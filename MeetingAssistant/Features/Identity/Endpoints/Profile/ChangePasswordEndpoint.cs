using Microsoft.AspNetCore.Mvc;
using MeetingAssistant.Shared.Abstractions;
using MeetingAssistant.Features.Identity.Models.Requests;
using Microsoft.AspNetCore.Authorization;
using System.Security.Claims;

namespace MeetingAssistant.Features.Identity.Endpoints.Profile
{
    public partial class ProfileController
    {
        [Authorize]
        [HttpPost("change-password")]
        public async Task<IActionResult> ChangePassword([FromBody] ChangePasswordRequest request, CancellationToken cancellationToken)
        {
            var userIdString = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (!Guid.TryParse(userIdString, out var userId))
            {
                return Unauthorized();
            }

            var result = await _profileService.ChangePasswordAsync(userId, request, cancellationToken);

            return result.IsSuccess 
                ? Ok()
                : result.ToProblem(_correlationIdProvider);
        }
    }
}
