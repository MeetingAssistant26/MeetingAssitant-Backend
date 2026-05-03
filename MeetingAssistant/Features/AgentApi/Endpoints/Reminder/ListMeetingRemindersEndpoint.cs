using MeetingAssistant.Shared.Abstractions;
using Microsoft.AspNetCore.Mvc;

namespace MeetingAssistant.Features.AgentApi.Endpoints.Reminder
{
    public partial class AgentReminderController
    {
        [HttpGet("meetings/{meetingId:guid}/reminders")]
        public async Task<IActionResult> ListMeetingReminders(
            [FromRoute] Guid meetingId,
            CancellationToken cancellationToken = default)
        {
            var contextResult = TryGetScopedMeeting(meetingId);
            if (contextResult.IsFailure)
            {
                return contextResult.ToProblem(_correlationIdProvider);
            }

            var result = await _agentReminderService.ListPublicMeetingRemindersAsync(
                contextResult.Value.OrganizationId,
                contextResult.Value.MeetingId,
                cancellationToken);

            return result.IsSuccess
                ? Ok(result.Value)
                : result.ToProblem(_correlationIdProvider);
        }
    }
}
