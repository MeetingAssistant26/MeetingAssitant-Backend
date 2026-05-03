using System.Security.Claims;
using MeetingAssistant.Api.Infrastructure.Services;
using MeetingAssistant.Shared.Abstractions;
using Microsoft.AspNetCore.Mvc;

namespace MeetingAssistant.Features.Tasks.Endpoints.Reminder
{
    public partial class ReminderController
    {
        [HttpDelete("{id:guid}")]
        public async Task<IActionResult> CancelMyReminder(
            [FromRoute] Guid id,
            CancellationToken cancellationToken = default)
        {
            var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
            var organizationId = _tenantProvider.CurrentOrganizationId
                ?? throw new UnauthorizedAccessException("No active organization context found.");

            var result = await _reminderService.CancelReminderAsync(id, userId, organizationId, cancellationToken);

            return result.IsSuccess
                ? NoContent()
                : result.ToProblem(_correlationIdProvider);
        }
    }
}
