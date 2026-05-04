using System.Security.Claims;
using MeetingAssistant.Api.Infrastructure.Services;
using MeetingAssistant.Shared.Abstractions;
using Microsoft.AspNetCore.Mvc;

namespace MeetingAssistant.Features.ActionItems.Endpoints.Review
{
    public partial class ActionItemReviewController
    {
        [HttpDelete("{id:guid}")]
        public async Task<IActionResult> DeleteActionItem(
            Guid organizationId,
            Guid meetingId,
            Guid id,
            [FromHeader(Name = "If-Match")] string etag,
            CancellationToken cancellationToken = default)
        {
            var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
            var result = await _actionItemService.DeleteAsync(id, userId, organizationId, etag, cancellationToken);

            return result.IsSuccess
                ? NoContent()
                : result.ToProblem(_correlationIdProvider);
        }
    }
}
