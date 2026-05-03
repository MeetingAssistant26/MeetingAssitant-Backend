using MeetingAssistant.Shared.Abstractions;
using Microsoft.AspNetCore.Mvc;

namespace MeetingAssistant.Features.AgentApi.Endpoints.Context
{
    public partial class AgentContextController
    {
        [HttpGet("meetings/{meetingId:guid}/members")]
        public async Task<IActionResult> GetMeetingMembers(
            [FromRoute] Guid meetingId,
            CancellationToken cancellationToken = default)
        {
            var contextResult = TryGetScopedMeeting(meetingId);
            if (contextResult.IsFailure)
            {
                return contextResult.ToProblem(_correlationIdProvider);
            }

            var result = await _agentContextService.GetMeetingMembersAsync(
                contextResult.Value.OrganizationId,
                contextResult.Value.MeetingId,
                cancellationToken);

            return result.IsSuccess
                ? Ok(result.Value)
                : result.ToProblem(_correlationIdProvider);
        }
    }
}
