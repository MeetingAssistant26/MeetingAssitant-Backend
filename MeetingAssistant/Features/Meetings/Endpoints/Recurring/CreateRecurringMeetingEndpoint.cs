using System.Security.Claims;
using MeetingAssistant.Features.Meetings.Contracts.Requests;
using Microsoft.AspNetCore.Mvc;
using MeetingAssistant.Shared.Abstractions;

namespace MeetingAssistant.Features.Meetings.Endpoints.Recurring
{
    public partial class RecurringMeetingController
    {
        [HttpPost]
        public async Task<IActionResult> Create(
            [FromRoute] Guid orgId,
            [FromBody] CreateRecurringMeetingRequest request,
            CancellationToken cancellationToken)
        {
            var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
            var result = await _recurrenceService.GenerateMeetingInstancesAsync(userId, request, cancellationToken);

            return result.IsSuccess
                ? Ok(result.Value)
                : result.ToProblem(_correlationIdProvider);
        }
    }
}