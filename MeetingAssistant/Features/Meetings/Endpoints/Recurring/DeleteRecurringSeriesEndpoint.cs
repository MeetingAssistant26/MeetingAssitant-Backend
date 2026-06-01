using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using MeetingAssistant.Shared.Abstractions;

namespace MeetingAssistant.Features.Meetings.Endpoints.Recurring
{
    public partial class RecurringMeetingController
    {
        [HttpDelete("{seriesId:guid}")]
        public async Task<IActionResult> Delete(
            [FromRoute] Guid orgId,
            [FromRoute] Guid seriesId,
            CancellationToken cancellationToken)
        {
            var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
            var result = await _recurrenceService.DeleteRecurringSeriesAsync(seriesId, userId, cancellationToken);

            return result.IsSuccess
                ? Ok(result.Value)
                : result.ToProblem(_correlationIdProvider);
        }
    }
}
