using System.Security.Claims;
using MeetingAssistant.Features.Meetings.Contracts.Requests;
using MeetingAssistant.Shared.Abstractions;
using Microsoft.AspNetCore.Mvc;

namespace MeetingAssistant.Features.Meetings.Endpoints.Meeting
{
    public partial class MeetingController
    {
        [HttpPut("{id:guid}")]
        public async Task<IActionResult> UpdateMeeting(
            [FromRoute] Guid orgId,
            [FromRoute] Guid id,
            [FromBody] UpdateMeetingRequest request,
            CancellationToken cancellationToken)
        {
            var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
            var result = await _meetingService.UpdateMeetingAsync(id, request, userId, cancellationToken);

            return result.IsSuccess
                ? Ok(result.Value)
                : result.ToProblem(_correlationIdProvider);
        }
    }
}
