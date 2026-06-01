using System.Security.Claims;
using MeetingAssistant.Features.LiveSession.Contracts.Responses;
using MeetingAssistant.Shared.Abstractions;
using Microsoft.AspNetCore.Mvc;

namespace MeetingAssistant.Features.LiveSession.Endpoints.AiAssistant
{
    public partial class AiAssistantController
    {
        [HttpPost("disable")]
        [ProducesResponseType(typeof(AiAssistantStatusResponse), StatusCodes.Status200OK)]
        public async Task<IActionResult> Disable(
            [FromRoute] Guid orgId,
            [FromRoute] Guid meetingId,
            CancellationToken cancellationToken)
        {
            var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
            var result = await _aiAssistantDispatchService.DisableAsync(
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
