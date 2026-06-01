using System.Security.Claims;
using MeetingAssistant.Shared.Abstractions;
using Microsoft.AspNetCore.Mvc;

namespace MeetingAssistant.Features.ActionItems.Endpoints.Organization
{
    public partial class OrganizationActionItemController
    {
        [HttpGet]
        public async Task<IActionResult> ListOrganizationActionItems(
            Guid orgId,
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 20,
            [FromQuery] string? assignee = null,
            [FromQuery] Guid? meetingId = null,
            [FromQuery] string? status = null,
            [FromQuery] string? provider = null,
            [FromQuery] DateTime? fromUtc = null,
            [FromQuery] DateTime? toUtc = null,
            CancellationToken cancellationToken = default)
        {
            var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
            var result = await _actionItemService.ListOrganizationActionItemsAsync(
                orgId,
                userId,
                page,
                pageSize,
                assignee,
                meetingId,
                status,
                provider,
                fromUtc,
                toUtc,
                cancellationToken);

            return result.IsSuccess
                ? Ok(result.Value)
                : result.ToProblem(_correlationIdProvider);
        }
    }
}
