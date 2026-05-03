using MeetingAssistant.Shared.Abstractions;
using Microsoft.AspNetCore.Mvc;

namespace MeetingAssistant.Features.AgentApi.Endpoints.Context
{
    public partial class AgentContextController
    {
        [HttpGet("meetings/{meetingId:guid}")]
        public async Task<IActionResult> GetMeetingDetail(
            [FromRoute] Guid meetingId,
            CancellationToken cancellationToken = default)
        {
            var orgResult = TryGetOrganizationId();
            if (orgResult.IsFailure)
            {
                return orgResult.ToProblem(_correlationIdProvider);
            }

            var result = await _agentContextService.GetMeetingDetailAsync(
                orgResult.Value,
                meetingId,
                cancellationToken);

            return result.IsSuccess
                ? Ok(result.Value)
                : result.ToProblem(_correlationIdProvider);
        }
    }
}
