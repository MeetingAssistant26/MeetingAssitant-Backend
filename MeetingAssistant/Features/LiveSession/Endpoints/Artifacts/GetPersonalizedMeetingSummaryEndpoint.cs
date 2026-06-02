using System.Security.Claims;
using MeetingAssistant.Features.LiveSession.Contracts.Responses;
using MeetingAssistant.Shared.Abstractions;
using Microsoft.AspNetCore.Mvc;

namespace MeetingAssistant.Features.LiveSession.Endpoints.Artifacts
{
    public partial class MeetingArtifactController
    {
        [HttpGet("summary/personalized")]
        [ProducesResponseType(typeof(PersonalizedMeetingSummaryResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<IActionResult> GetPersonalizedSummary(
            [FromRoute] Guid orgId,
            [FromRoute] Guid meetingId,
            CancellationToken cancellationToken)
        {
            var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
            var result = await _meetingArtifactService.GetPersonalizedSummaryAsync(orgId, meetingId, userId, cancellationToken);

            return result.IsSuccess
                ? Ok(result.Value)
                : result.ToProblem(_correlationIdProvider);
        }
    }
}
