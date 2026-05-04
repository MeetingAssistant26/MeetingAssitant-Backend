using System.Security.Claims;
using MeetingAssistant.Api.Infrastructure.Services;
using MeetingAssistant.Shared.Abstractions;
using Microsoft.AspNetCore.Mvc;

namespace MeetingAssistant.Features.ActionItems.Endpoints.Review
{
    public partial class ActionItemReviewController
    {
        [HttpPost("re-extract")]
        public async Task<IActionResult> ReExtractActionItems(
            Guid organizationId,
            Guid meetingId,
            CancellationToken cancellationToken = default)
        {
            var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
            var result = await _actionItemService.ReExtractAsync(meetingId, userId, organizationId, cancellationToken);

            return result.IsSuccess
                ? Accepted()
                : result.ToProblem(_correlationIdProvider);
        }
    }
}
