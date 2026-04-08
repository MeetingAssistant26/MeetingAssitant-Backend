using MeetingAssistant.Features.Organizations.Contracts.Requests;
using MeetingAssistant.Shared.Abstractions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MeetingAssistant.Features.Organizations.Endpoints.MeetingTag
{
    public partial class MeetingTagController
    {
        [HttpPut("{tagId:guid}")]
        [Authorize(Policy = "RequireOrgAdmin")]
        public async Task<IActionResult> UpdateMeetingTag(
            [FromRoute] Guid orgId,
            [FromRoute] Guid tagId,
            [FromBody] UpdateMeetingTagRequest request,
            CancellationToken cancellationToken)
        {
            var result = await _meetingTagService.UpdateAsync(tagId, request, cancellationToken);

            return result.IsSuccess
                ? Ok(result.Value)
                : result.ToProblem(_correlationIdProvider);
        }
    }
}
