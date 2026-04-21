using System.Security.Claims;
using MeetingAssistant.Features.LiveSession.Contracts.Requests;
using MeetingAssistant.Shared.Abstractions;
using Microsoft.AspNetCore.Mvc;

namespace MeetingAssistant.Features.LiveSession.Endpoints.Session
{
    public partial class SessionController
    {
        [HttpPost("join-token")]
        public async Task<IActionResult> GetJoinToken(
            [FromRoute] Guid orgId,
            [FromRoute] Guid meetingId,
            [FromBody] JoinTokenRequest request,
            CancellationToken cancellationToken)
        {
            var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

            var result = await _sessionService.IssueJoinTokenAsync(
                meetingId,
                userId,
                request.DisplayName,
                cancellationToken);

            return result.IsSuccess
                ? Ok(result.Value)
                : result.ToProblem(_correlationIdProvider);
        }
    }
}
