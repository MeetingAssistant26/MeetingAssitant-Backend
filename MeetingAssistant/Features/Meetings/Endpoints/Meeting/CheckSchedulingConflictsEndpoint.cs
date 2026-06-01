using System.Security.Claims;
using MeetingAssistant.Features.Meetings.Contracts.Requests;
using MeetingAssistant.Shared.Abstractions;
using Microsoft.AspNetCore.Mvc;

namespace MeetingAssistant.Features.Meetings.Endpoints.Meeting
{
    public partial class MeetingController
    {
        [HttpPost("conflict-check")]
        public async Task<IActionResult> CheckSchedulingConflicts(
            [FromRoute] Guid orgId,
            [FromBody] MeetingConflictCheckRequest request,
            CancellationToken cancellationToken)
        {
            var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
            var result = await _meetingService.CheckSchedulingConflictsAsync(request, userId, cancellationToken);

            return result.IsSuccess
                ? Ok(result.Value)
                : result.ToProblem(_correlationIdProvider);
        }
    }
}
