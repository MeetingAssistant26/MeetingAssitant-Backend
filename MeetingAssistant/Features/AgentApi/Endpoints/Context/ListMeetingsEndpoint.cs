using MeetingAssistant.Shared.Abstractions;
using Microsoft.AspNetCore.Mvc;

namespace MeetingAssistant.Features.AgentApi.Endpoints.Context
{
    public partial class AgentContextController
    {
        [HttpGet("meetings")]
        public async Task<IActionResult> ListMeetings(
            [FromQuery] string? status = "upcoming",
            [FromQuery] int limit = 20,
            [FromQuery] int offset = 0,
            CancellationToken cancellationToken = default)
        {
            var orgResult = TryGetOrganizationId();
            if (orgResult.IsFailure)
            {
                return orgResult.ToProblem(_correlationIdProvider);
            }

            var result = await _agentContextService.ListMeetingsAsync(
                orgResult.Value,
                status,
                limit,
                offset,
                cancellationToken);

            return result.IsSuccess
                ? Ok(result.Value)
                : result.ToProblem(_correlationIdProvider);
        }
    }
}
