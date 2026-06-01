using System.Security.Claims;
using MeetingAssistant.Features.Meetings.Contracts.Responses;
using MeetingAssistant.Shared.Abstractions;
using Microsoft.AspNetCore.Mvc;

namespace MeetingAssistant.Features.Meetings.Endpoints.TagSuggestions
{
    public partial class MeetingTagSuggestionController
    {
        [HttpPost("reindex")]
        [ProducesResponseType(typeof(MeetingTagMetadataRefreshResponse), StatusCodes.Status202Accepted)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<IActionResult> RefreshMeetingTagMetadata(
            [FromRoute] Guid orgId,
            [FromRoute] Guid meetingId,
            CancellationToken cancellationToken = default)
        {
            var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
            var result = await _tagSuggestionReviewService.RefreshTagMetadataAsync(orgId, meetingId, userId, cancellationToken);

            return result.IsSuccess
                ? Accepted(result.Value)
                : result.ToProblem(_correlationIdProvider);
        }
    }
}
