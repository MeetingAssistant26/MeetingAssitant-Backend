using MeetingAssistant.Shared.Abstractions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MeetingAssistant.Features.Organizations.Endpoints.MeetingTag
{
    public partial class MeetingTagController
    {
        [HttpDelete("{tagId:guid}")]
        [Authorize(Policy = "RequireOrgAdmin")]
        public async Task<IActionResult> DeleteMeetingTag(            
            [FromRoute] Guid tagId,
            CancellationToken cancellationToken)
        {
            var result = await _meetingTagService.DeleteAsync(tagId, cancellationToken);

            return result.IsSuccess
                ? NoContent()
                : result.ToProblem(_correlationIdProvider);
        }
    }
}
