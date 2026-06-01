using System.Security.Claims;
using MeetingAssistant.Features.LiveSession.Contracts.AiDebug;
using MeetingAssistant.Shared.Abstractions;
using Microsoft.AspNetCore.Mvc;

namespace MeetingAssistant.Features.LiveSession.Endpoints.AiDebug
{
    public partial class AiDebugTraceController
    {
        [HttpGet]
        [ProducesResponseType(typeof(AiDebugTraceResponse), StatusCodes.Status200OK)]
        public async Task<IActionResult> GetAiDebugTraces(
            [FromRoute] Guid orgId,
            [FromRoute] Guid meetingId,
            CancellationToken cancellationToken = default)
        {
            var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
            var result = await _aiDebugTraceService.GetTracesAsync(
                orgId,
                meetingId,
                userId,
                cancellationToken);

            return result.IsSuccess
                ? Ok(result.Value)
                : result.ToProblem(_correlationIdProvider);
        }
    }
}
