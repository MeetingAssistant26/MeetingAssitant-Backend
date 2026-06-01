using System.Security.Claims;
using MeetingAssistant.Features.LiveSession.Contracts.Responses;
using MeetingAssistant.Shared.Abstractions;
using Microsoft.AspNetCore.Mvc;

namespace MeetingAssistant.Features.LiveSession.Endpoints.AiAssistant
{
    public partial class AiAssistantController
    {
        [HttpPost("enable")]
        [ProducesResponseType(typeof(AiAssistantStatusResponse), StatusCodes.Status200OK)]
        public async Task<IActionResult> Enable(
            [FromRoute] Guid orgId,
            [FromRoute] Guid meetingId,
            CancellationToken cancellationToken)
        {
            var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
            var result = await _aiAssistantDispatchService.EnableAsync(
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
