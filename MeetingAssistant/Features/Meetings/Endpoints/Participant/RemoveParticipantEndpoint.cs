using Microsoft.AspNetCore.Mvc;
using MeetingAssistant.Shared.Abstractions;
using System.Security.Claims;

namespace MeetingAssistant.Features.Meetings.Endpoints.Participant
{
    public partial class ParticipantController
    {
        [HttpDelete("{userId:guid}")]
        public async Task<IActionResult> RemoveParticipant(
            [FromRoute] Guid meetingId,
            [FromRoute] Guid userId,
            CancellationToken cancellationToken)
        {
            var callerId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

            var result = await _participantService.RemoveParticipantAsync(meetingId, userId, callerId, cancellationToken);

            return result.IsSuccess 
                ? NoContent() 
                : result.ToProblem(_correlationIdProvider);
        }
    }
}