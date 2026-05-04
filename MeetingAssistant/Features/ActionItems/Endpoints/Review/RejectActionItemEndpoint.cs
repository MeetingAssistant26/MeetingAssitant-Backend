using System.Security.Claims;
using MeetingAssistant.Api.Infrastructure.Services;
using MeetingAssistant.Shared.Abstractions;
using Microsoft.AspNetCore.Mvc;

namespace MeetingAssistant.Features.ActionItems.Endpoints.Review
{
    public partial class ActionItemReviewController
    {
        [HttpPatch("{id:guid}/reject")]
        public async Task<IActionResult> RejectActionItem(
            Guid organizationId,
            Guid meetingId,
            Guid id,
            [FromHeader(Name = "If-Match")] string etag,
            CancellationToken cancellationToken = default)
        {
            var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
            var result = await _actionItemService.RejectAsync(id, userId, organizationId, etag, cancellationToken);

            return result.IsSuccess
                ? Ok(result.Value)
                : result.ToProblem(_correlationIdProvider);
        }
    }
}
