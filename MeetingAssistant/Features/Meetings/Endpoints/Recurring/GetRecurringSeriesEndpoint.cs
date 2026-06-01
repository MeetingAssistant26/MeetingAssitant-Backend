using Microsoft.AspNetCore.Mvc;
using MeetingAssistant.Shared.Abstractions;

namespace MeetingAssistant.Features.Meetings.Endpoints.Recurring
{
    public partial class RecurringMeetingController
    {
        [HttpGet("{seriesId:guid}")]
        public async Task<IActionResult> Get(
            [FromRoute] Guid orgId,
            [FromRoute] Guid seriesId,
            CancellationToken cancellationToken)
        {
            var result = await _recurrenceService.GetRecurringSeriesAsync(seriesId, cancellationToken);

            return result.IsSuccess
                ? Ok(result.Value)
                : result.ToProblem(_correlationIdProvider);
        }
    }
}
