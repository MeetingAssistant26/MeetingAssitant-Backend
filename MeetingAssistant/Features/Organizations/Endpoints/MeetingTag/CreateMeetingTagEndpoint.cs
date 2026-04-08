using MeetingAssistant.Features.Organizations.Contracts.Requests;
using MeetingAssistant.Shared.Abstractions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MeetingAssistant.Features.Organizations.Endpoints.MeetingTag
{
    public partial class MeetingTagController
    {
        [HttpPost]
        [Authorize(Policy = "RequireOrgAdmin")]
        public async Task<IActionResult> CreateMeetingTag(
            [FromRoute] Guid orgId,
            [FromBody] CreateMeetingTagRequest request,
            CancellationToken cancellationToken)
        {
            var result = await _meetingTagService.CreateAsync(request, cancellationToken);

            return result.IsSuccess
                ? CreatedAtAction(nameof(ListMeetingTags), new { orgId }, result.Value)
                : result.ToProblem(_correlationIdProvider);
        }
    }
}
