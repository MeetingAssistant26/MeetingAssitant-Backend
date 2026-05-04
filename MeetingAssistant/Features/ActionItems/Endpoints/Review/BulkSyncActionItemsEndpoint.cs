using System.Security.Claims;
using MeetingAssistant.Api.Infrastructure.Services;
using MeetingAssistant.Features.ActionItems.Models.Requests;
using MeetingAssistant.Shared.Abstractions;
using Microsoft.AspNetCore.Mvc;

namespace MeetingAssistant.Features.ActionItems.Endpoints.Review
{
    public partial class ActionItemReviewController
    {
        [HttpPost("sync-all")]
        public async Task<IActionResult> BulkSyncActionItems(
            Guid organizationId,
            Guid meetingId,
            [FromBody] BulkSyncRequest request,
            CancellationToken cancellationToken = default)
        {
            var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
            var result = await _actionItemService.BulkSyncAsync(meetingId, request.ActionItemIds, userId, organizationId, cancellationToken);

            return result.IsSuccess
                ? StatusCode(207, result.Value)
                : result.ToProblem(_correlationIdProvider);
        }
    }
}
