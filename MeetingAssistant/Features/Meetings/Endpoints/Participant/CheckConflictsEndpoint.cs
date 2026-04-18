using MeetingAssistant.Features.Meetings.Contracts.Responses;
using MeetingAssistant.Shared.Abstractions;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

namespace MeetingAssistant.Features.Meetings.Endpoints.Participant
{
    public partial class ParticipantController
    {
        [HttpGet("conflicts")]
        public async Task<IActionResult> CheckConflicts(
            [FromRoute] Guid meetingId,
            CancellationToken cancellationToken)
        {
            var callerId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

            var result = await _participantService.CheckConflictsAsync(meetingId, callerId, cancellationToken);

            return result.IsSuccess
                ? Ok(new ConflictCheckResponse(result.Value))
                : result.ToProblem(_correlationIdProvider);
        }
    }
}
