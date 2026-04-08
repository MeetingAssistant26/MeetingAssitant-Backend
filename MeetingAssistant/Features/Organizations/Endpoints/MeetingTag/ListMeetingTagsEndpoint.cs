using MeetingAssistant.Shared.Abstractions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MeetingAssistant.Features.Organizations.Endpoints.MeetingTag
{
    public partial class MeetingTagController
    {
        [HttpGet]
        [Authorize(Policy = "RequireOrgAccess")]
        public async Task<IActionResult> ListMeetingTags(
            CancellationToken cancellationToken)
        {
            var result = await _meetingTagService.ListAsync(cancellationToken);

            return result.IsSuccess
                ? Ok(result.Value)
                : result.ToProblem(_correlationIdProvider);
        }
    }
}
