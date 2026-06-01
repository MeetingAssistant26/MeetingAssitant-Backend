using System.Security.Claims;
using MeetingAssistant.Api.Infrastructure.Services;
using MeetingAssistant.Features.Tasks.Contracts.Requests;
using MeetingAssistant.Features.Tasks.Contracts.Responses;
using MeetingAssistant.Shared.Abstractions;
using Microsoft.AspNetCore.Mvc;

namespace MeetingAssistant.Features.Tasks.Endpoints.Reminder
{
    public partial class ReminderController
    {
        [HttpPatch("{id:guid}")]
        [ProducesResponseType(typeof(ReminderResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        [ProducesResponseType(StatusCodes.Status409Conflict)]
        public async Task<IActionResult> UpdateMyReminder(
            [FromRoute] Guid id,
            [FromBody] UpdateMyReminderRequest request,
            CancellationToken cancellationToken = default)
        {
            var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
            var organizationId = _tenantProvider.CurrentOrganizationId
                ?? throw new UnauthorizedAccessException("No active organization context found.");

            var result = await _reminderService.UpdateReminderAsync(id, request, userId, organizationId, cancellationToken);

            return result.IsSuccess
                ? Ok(result.Value)
                : result.ToProblem(_correlationIdProvider);
        }
    }
}
