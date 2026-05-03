using System.Security.Claims;
using MeetingAssistant.Api.Infrastructure.Services;
using MeetingAssistant.Shared.Abstractions;
using Microsoft.AspNetCore.Mvc;

namespace MeetingAssistant.Features.Tasks.Endpoints.Reminder
{
    public partial class ReminderController
    {
        [HttpPost("{id:guid}/mark-delivered")]
        public async Task<IActionResult> MarkMyReminderDelivered(
            [FromRoute] Guid id,
            CancellationToken cancellationToken = default)
        {
            var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
            var organizationId = _tenantProvider.CurrentOrganizationId
                ?? throw new UnauthorizedAccessException("No active organization context found.");

            var result = await _reminderService.MarkDeliveredAsync(id, userId, organizationId, cancellationToken);

            return result.IsSuccess
                ? Ok(result.Value)
                : result.ToProblem(_correlationIdProvider);
        }
    }
}
