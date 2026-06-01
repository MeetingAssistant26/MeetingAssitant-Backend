using System.Security.Claims;
using MeetingAssistant.Features.Organizations.Infrastructure.Filters;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MeetingAssistant.Shared.Abstractions;

namespace MeetingAssistant.Features.Organizations.Endpoints.Organization
{
    public partial class OrganizationController
    {
        [HttpPost("{orgId:guid}/leave")]
        [Authorize(Policy = "RequireOrgAccess")]
        [EnforceOrgAccess]
        public async Task<IActionResult> LeaveOrganization(
            [FromRoute] Guid orgId,
            CancellationToken cancellationToken)
        {
            var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
            var result = await _organizationService.LeaveOrganizationAsync(orgId, userId, cancellationToken);

            return result.IsSuccess
                ? NoContent()
                : result.ToProblem(_correlationIdProvider);
        }
    }
}
