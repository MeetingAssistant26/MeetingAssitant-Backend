using MeetingAssistant.Shared.Abstractions;
using Microsoft.AspNetCore.Mvc;

namespace MeetingAssistant.Features.Meetings.Endpoints.Calendar
{
    public partial class CalendarController
    {
        [HttpGet]
        public async Task<IActionResult> GetCalendarData(
            [FromRoute] Guid orgId,
            [FromQuery] DateTime? week,
            CancellationToken cancellationToken)
        {
            var result = await _calendarService.GetCalendarDataAsync(orgId, week, cancellationToken);
            
            return result.IsSuccess 
                ? Ok(result.Value) 
                : result.ToProblem(_correlationIdProvider);
        }
    }
}
