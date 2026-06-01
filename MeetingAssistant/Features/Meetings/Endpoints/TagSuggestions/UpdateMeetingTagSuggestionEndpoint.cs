using System.Security.Claims;
using MeetingAssistant.Features.Meetings.Contracts.Requests;
using MeetingAssistant.Features.Meetings.Contracts.Responses;
using MeetingAssistant.Shared.Abstractions;
using Microsoft.AspNetCore.Mvc;

namespace MeetingAssistant.Features.Meetings.Endpoints.TagSuggestions
{
    public partial class MeetingTagSuggestionController
    {
        [HttpPatch("{suggestionId:guid}")]
        [ProducesResponseType(typeof(MeetingTagSuggestionResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        [ProducesResponseType(StatusCodes.Status409Conflict)]
        public async Task<IActionResult> UpdateMeetingTagSuggestion(
            [FromRoute] Guid orgId,
            [FromRoute] Guid meetingId,
            [FromRoute] Guid suggestionId,
            [FromBody] UpdateMeetingTagSuggestionRequest request,
            CancellationToken cancellationToken = default)
        {
            var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
            var result = await _tagSuggestionReviewService.UpdateAsync(
                orgId,
                meetingId,
                suggestionId,
                userId,
                request,
                cancellationToken);

            return result.IsSuccess
                ? Ok(result.Value)
                : result.ToProblem(_correlationIdProvider);
        }
    }
}
