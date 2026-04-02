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
        [HttpPut]
        public async Task<IActionResult> UpdateProfile([FromBody] UpdateProfileRequest request, CancellationToken cancellationToken)
        {
            var userIdString = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (!Guid.TryParse(userIdString, out var userId))
            {
                return Unauthorized();
            }

            var result = await _profileService.UpdateProfileAsync(userId, request, cancellationToken);

            return result.IsSuccess 
                ? Ok(result.Value)
                : result.ToProblem(_correlationIdProvider);
        }
    }
}
