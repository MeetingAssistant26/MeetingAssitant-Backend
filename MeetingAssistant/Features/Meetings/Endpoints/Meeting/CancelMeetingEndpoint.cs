using System.Security.Claims;
using MeetingAssistant.Shared.Abstractions;
using Microsoft.AspNetCore.Mvc;

namespace MeetingAssistant.Features.Meetings.Endpoints.Meeting
{
    public partial class MeetingController
    {
        [HttpDelete("{id:guid}")]
        public async Task<IActionResult> CancelMeeting(
            [FromRoute] Guid orgId,
            [FromRoute] Guid id,
            CancellationToken cancellationToken)
        {
            var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
            var result = await _meetingService.CancelMeetingAsync(id, userId, cancellationToken);

            return result.IsSuccess
                ? Ok(new { id, status = "Cancelled" })
                : result.ToProblem(_correlationIdProvider);
        }
    }
}
