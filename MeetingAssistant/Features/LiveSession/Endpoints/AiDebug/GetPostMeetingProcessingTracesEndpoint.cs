using MeetingAssistant.Features.LiveSession.Contracts.AiDebug;
using MeetingAssistant.Shared.Abstractions;
using Microsoft.AspNetCore.Mvc;

namespace MeetingAssistant.Features.LiveSession.Endpoints.AiDebug
{
    public partial class PostMeetingProcessingTraceController
    {
        [HttpGet]
        [ProducesResponseType(typeof(PostMeetingProcessingTraceResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<IActionResult> GetPostMeetingProcessingTraces(
            [FromRoute] Guid orgId,
            [FromRoute] Guid meetingId,
            CancellationToken cancellationToken = default)
        {
            var result = await _postMeetingProcessingTraceService.GetTracesAsync(
                orgId,
                meetingId,
                cancellationToken);

            return result.IsSuccess
                ? Ok(result.Value)
                : result.ToProblem(_correlationIdProvider);
        }
    }
}
