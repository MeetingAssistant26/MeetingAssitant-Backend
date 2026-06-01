using Microsoft.AspNetCore.Mvc;
using MeetingAssistant.Shared.Abstractions;

namespace MeetingAssistant.Features.Meetings.Endpoints.Recurring
{
    public partial class RecurringMeetingController
    {
        [HttpGet]
        public async Task<IActionResult> List(
            [FromRoute] Guid orgId,
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 20,
            CancellationToken cancellationToken = default)
        {
            var result = await _recurrenceService.ListRecurringSeriesAsync(page, pageSize, cancellationToken);

            return result.IsSuccess
                ? Ok(result.Value)
                : result.ToProblem(_correlationIdProvider);
        }
    }
}
