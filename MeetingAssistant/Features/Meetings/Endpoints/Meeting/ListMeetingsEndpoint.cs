using MeetingAssistant.Shared.Abstractions;
using Microsoft.AspNetCore.Mvc;

namespace MeetingAssistant.Features.Meetings.Endpoints.Meeting
{
    public partial class MeetingController
    {
        [HttpGet]
        public async Task<IActionResult> ListMeetings(
            [FromRoute] Guid orgId,
            [FromQuery] string? filter,
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 20,
            CancellationToken cancellationToken = default)
        {
            if (pageSize > 100) pageSize = 100;
            if (page < 1) page = 1;

            var result = await _meetingService.ListMeetingsAsync(filter, page, pageSize, cancellationToken);

            return result.IsSuccess
                ? Ok(result.Value)
                : result.ToProblem(_correlationIdProvider);
        }
    }
}
