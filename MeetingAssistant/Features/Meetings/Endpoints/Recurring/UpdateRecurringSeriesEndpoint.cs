using System.Security.Claims;
using MeetingAssistant.Features.Meetings.Contracts.Requests;
using Microsoft.AspNetCore.Mvc;
using MeetingAssistant.Shared.Abstractions;

namespace MeetingAssistant.Features.Meetings.Endpoints.Recurring
{
    public partial class RecurringMeetingController
    {
        [HttpPatch("{seriesId:guid}")]
        [HttpPut("{seriesId:guid}")]
        public async Task<IActionResult> Update(
            [FromRoute] Guid orgId,
            [FromRoute] Guid seriesId,
            [FromBody] UpdateRecurringSeriesRequest request,
            CancellationToken cancellationToken)
        {
            var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
            var result = await _recurrenceService.UpdateRecurringSeriesAsync(seriesId, userId, request, cancellationToken);

            return result.IsSuccess
                ? Ok(result.Value)
                : result.ToProblem(_correlationIdProvider);
        }
    }
}
