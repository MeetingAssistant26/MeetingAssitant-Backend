using Microsoft.AspNetCore.Mvc;
using MeetingAssistant.Shared.Abstractions;
using MeetingAssistant.Features.Identity.Models.Responses;
using Microsoft.AspNetCore.Authorization;
using System.Security.Claims;

namespace MeetingAssistant.Features.Identity.Endpoints.Profile
{
    public partial class ProfileController
    {
        [Authorize]
        [HttpGet]
        public async Task<IActionResult> GetCurrentUser(CancellationToken cancellationToken)
        {
            var userIdString = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (!Guid.TryParse(userIdString, out var userId))
            {
                return Unauthorized();
            }

            var result = await _profileService.GetProfileAsync(userId, cancellationToken);

            return result.IsSuccess 
                ? Ok(result.Value)
                : result.ToProblem(_correlationIdProvider);
        }
    }
}
