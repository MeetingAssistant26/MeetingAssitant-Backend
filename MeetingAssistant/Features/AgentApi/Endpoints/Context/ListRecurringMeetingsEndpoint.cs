using MeetingAssistant.Shared.Abstractions;
using Microsoft.AspNetCore.Mvc;

namespace MeetingAssistant.Features.AgentApi.Endpoints.Context
{
    public partial class AgentContextController
    {
        [HttpGet("meetings/recurring")]
        public async Task<IActionResult> ListRecurringMeetings(
            CancellationToken cancellationToken = default)
        {
            var orgResult = TryGetOrganizationId();
            if (orgResult.IsFailure)
            {
                return orgResult.ToProblem(_correlationIdProvider);
            }

            var result = await _agentContextService.ListRecurringMeetingsAsync(
                orgResult.Value,
                cancellationToken);

            return result.IsSuccess
                ? Ok(result.Value)
                : result.ToProblem(_correlationIdProvider);
        }
    }
}
