using MeetingAssistant.Features.LiveSession.Contracts.Responses;
using MeetingAssistant.Shared.Abstractions;
using Microsoft.AspNetCore.Mvc;

namespace MeetingAssistant.Features.LiveSession.Endpoints.Artifacts
{
    public partial class MeetingArtifactController
    {
        [HttpGet("summary")]
        [ProducesResponseType(typeof(MeetingSummaryResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<IActionResult> GetSummary(
            [FromRoute] Guid orgId,
            [FromRoute] Guid meetingId,
            CancellationToken cancellationToken)
        {
            var result = await _meetingArtifactService.GetSummaryAsync(orgId, meetingId, cancellationToken);

            return result.IsSuccess
                ? Ok(result.Value)
                : result.ToProblem(_correlationIdProvider);
        }
    }
}
