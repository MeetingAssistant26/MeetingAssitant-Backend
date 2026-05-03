using MeetingAssistant.Shared.Abstractions;
using Microsoft.AspNetCore.Mvc;

namespace MeetingAssistant.Features.AgentApi.Endpoints.Context
{
    public partial class AgentContextController
    {
        [HttpGet("meeting-tags")]
        public async Task<IActionResult> ListMeetingTags(
            CancellationToken cancellationToken = default)
        {
            var orgResult = TryGetOrganizationId();
            if (orgResult.IsFailure)
            {
                return orgResult.ToProblem(_correlationIdProvider);
            }

            var result = await _agentContextService.ListMeetingTagsAsync(
                orgResult.Value,
                cancellationToken);

            return result.IsSuccess
                ? Ok(result.Value)
                : result.ToProblem(_correlationIdProvider);
        }
    }
}
