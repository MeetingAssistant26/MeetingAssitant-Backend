using MeetingAssistant.Shared.Abstractions;
using Microsoft.AspNetCore.Mvc;

namespace MeetingAssistant.Features.AgentApi.Endpoints.Reminder
{
    public partial class AgentReminderController
    {
        [HttpDelete("reminders/{id:guid}")]
        public async Task<IActionResult> CancelReminder(
            [FromRoute] Guid id,
            CancellationToken cancellationToken = default)
        {
            var orgResult = TryGetOrganizationId();
            if (orgResult.IsFailure)
            {
                return orgResult.ToProblem(_correlationIdProvider);
            }

            var meetingId = _agentContextProvider.CurrentMeetingId
                ?? throw new UnauthorizedAccessException("No active meeting context found.");

            var result = await _agentReminderService.CancelReminderAsync(
                id,
                orgResult.Value,
                meetingId,
                cancellationToken);

            return result.IsSuccess
                ? NoContent()
                : result.ToProblem(_correlationIdProvider);
        }
    }
}
