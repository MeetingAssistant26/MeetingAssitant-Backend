using MeetingAssistant.Features.AgentApi.Models.Requests;
using MeetingAssistant.Shared.Abstractions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace MeetingAssistant.Features.AgentApi.Endpoints.Reminder
{
    public partial class AgentReminderController
    {
        [HttpPost("meetings/{meetingId:guid}/reminders")]
        public async Task<IActionResult> CreateReminder(
            [FromRoute] Guid meetingId,
            [FromBody] CreateAgentReminderRequest request,
            CancellationToken cancellationToken = default)
        {
            var contextResult = TryGetScopedMeeting(meetingId);
            if (contextResult.IsFailure)
            {
                return contextResult.ToProblem(_correlationIdProvider);
            }

            var result = await _agentReminderService.CreateReminderAsync(
                request,
                contextResult.Value.OrganizationId,
                contextResult.Value.MeetingId,
                cancellationToken);

            return result.IsSuccess
                ? StatusCode(StatusCodes.Status201Created, result.Value)
                : result.ToProblem(_correlationIdProvider);
        }
    }
}
