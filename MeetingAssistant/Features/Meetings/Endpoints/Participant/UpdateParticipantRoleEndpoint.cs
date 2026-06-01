using System.Security.Claims;
using MeetingAssistant.Features.Meetings.Contracts.Requests;
using MeetingAssistant.Features.Meetings.Contracts.Responses;
using MeetingAssistant.Features.Organizations.Infrastructure.Filters;
using MeetingAssistant.Shared.Abstractions;
using Microsoft.AspNetCore.Mvc;

namespace MeetingAssistant.Features.Meetings.Endpoints.Participant
{
    public partial class ParticipantController
    {
        // New shared web/mobile contract is org-scoped. Existing non-org participant
        // add/remove routes are preserved for backward compatibility.
        [HttpPut("/api/organizations/{orgId:guid}/meetings/{meetingId:guid}/participants/{userId:guid}/role")]
        [EnforceOrgAccess]
        [ProducesResponseType(typeof(ParticipantResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<IActionResult> UpdateParticipantRole(
            [FromRoute] Guid orgId,
            [FromRoute] Guid meetingId,
            [FromRoute] Guid userId,
            [FromBody] UpdateParticipantRoleRequest request,
            CancellationToken cancellationToken)
        {
            var callerId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

            var result = await _participantService.UpdateParticipantRoleAsync(
                meetingId,
                userId,
                request,
                callerId,
                cancellationToken);

            return result.IsSuccess
                ? Ok(result.Value)
                : result.ToProblem(_correlationIdProvider);
        }
    }
}
