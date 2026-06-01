using MeetingAssistant.Features.AgentApi.Models.Requests;
using MeetingAssistant.Shared.Abstractions;
using Microsoft.AspNetCore.Mvc;

namespace MeetingAssistant.Features.AgentApi.Endpoints.Context
{
    public partial class AgentContextController
    {
        [HttpPost("meetings/{meetingId:guid}/context/query")]
        public async Task<IActionResult> QueryMeetingContext(
            [FromRoute] Guid meetingId,
            [FromBody] AgentMeetingContextQueryRequest request,
            CancellationToken cancellationToken = default)
        {
            var contextResult = TryGetScopedMeeting(meetingId);
            if (contextResult.IsFailure)
            {
                return contextResult.ToProblem(_correlationIdProvider);
            }

            var result = await _agentContextService.QueryMeetingContextAsync(
                request,
                contextResult.Value.OrganizationId,
                contextResult.Value.MeetingId,
                cancellationToken);

            return result.IsSuccess
                ? Ok(result.Value)
                : result.ToProblem(_correlationIdProvider);
        }
    }
}
