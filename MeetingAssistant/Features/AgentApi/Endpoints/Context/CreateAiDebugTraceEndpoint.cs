using MeetingAssistant.Features.LiveSession.Contracts.AiDebug;
using MeetingAssistant.Features.LiveSession.Services;
using MeetingAssistant.Shared.Abstractions;
using Microsoft.AspNetCore.Mvc;

namespace MeetingAssistant.Features.AgentApi.Endpoints.Context
{
    public partial class AgentContextController
    {
        [HttpPost("meetings/{meetingId:guid}/ai-debug/traces")]
        public async Task<IActionResult> CreateAiDebugTrace(
            [FromRoute] Guid meetingId,
            [FromBody] AiDebugTraceIngestRequest request,
            [FromServices] IAiDebugTraceService aiDebugTraceService,
            CancellationToken cancellationToken = default)
        {
            var contextResult = TryGetScopedMeeting(meetingId);
            if (contextResult.IsFailure)
            {
                return contextResult.ToProblem(_correlationIdProvider);
            }

            var result = await aiDebugTraceService.IngestAsync(
                contextResult.Value.OrganizationId,
                contextResult.Value.MeetingId,
                request,
                cancellationToken);

            return result.IsSuccess
                ? NoContent()
                : result.ToProblem(_correlationIdProvider);
        }
    }
}
