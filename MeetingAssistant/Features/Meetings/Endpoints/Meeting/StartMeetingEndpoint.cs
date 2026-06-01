using System.Security.Claims;
using MeetingAssistant.Features.Meetings.Contracts.Responses;
using MeetingAssistant.Shared.Abstractions;
using Microsoft.AspNetCore.Mvc;

namespace MeetingAssistant.Features.Meetings.Endpoints.Meeting
{
    public partial class MeetingController
    {
        [HttpPost("{meetingId:guid}/start")]
        [ProducesResponseType(typeof(MeetingResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        [ProducesResponseType(StatusCodes.Status409Conflict)]
        public async Task<IActionResult> StartMeeting(
            [FromRoute] Guid orgId,
            [FromRoute] Guid meetingId,
            CancellationToken cancellationToken = default)
        {
            var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
            var result = await _meetingService.StartMeetingAsync(meetingId, userId, cancellationToken);

            return result.IsSuccess
                ? Ok(result.Value)
                : result.ToProblem(_correlationIdProvider);
        }
    }
}
