using System.Security.Claims;
using MeetingAssistant.Api.Infrastructure.Services;
using MeetingAssistant.Features.Tasks.Contracts.Requests;
using MeetingAssistant.Shared.Abstractions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace MeetingAssistant.Features.Tasks.Endpoints.Reminder
{
    public partial class ReminderController
    {
        [HttpPost]
        public async Task<IActionResult> CreateMyReminder(
            [FromBody] CreateMyReminderRequest request,
            CancellationToken cancellationToken = default)
        {
            var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
            var organizationId = _tenantProvider.CurrentOrganizationId
                ?? throw new UnauthorizedAccessException("No active organization context found.");

            var result = await _reminderService.CreateReminderAsync(request, userId, organizationId, cancellationToken);

            return result.IsSuccess
                ? StatusCode(StatusCodes.Status201Created, result.Value)
                : result.ToProblem(_correlationIdProvider);
        }
    }
}
