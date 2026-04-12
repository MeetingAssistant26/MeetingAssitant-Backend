using MeetingAssistant.Features.Meetings.Contracts.Requests;
using Microsoft.AspNetCore.Mvc;
using MeetingAssistant.Shared.Abstractions;
using System.Security.Claims;

namespace MeetingAssistant.Features.Meetings.Endpoints.Participant
{
    public partial class ParticipantController
    {
        [HttpPost]
        public async Task<IActionResult> AddParticipant(
            [FromRoute] Guid meetingId,
            [FromBody] AddParticipantRequest request,
            CancellationToken cancellationToken)
        {
            var callerId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

            var result = await _participantService.AddParticipantAsync(meetingId, request, callerId, cancellationToken);

            return result.IsSuccess 
                ? Ok(result.Value) 
                : result.ToProblem(_correlationIdProvider);
        }
    }
}