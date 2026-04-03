using MeetingAssistant.Features.Organizations.Contracts.Requests;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MeetingAssistant.Shared.Abstractions;

namespace MeetingAssistant.Features.Organizations.Endpoints.Member
{
    public partial class MemberController
    {
        [HttpPut("{userId:guid}/role")]
        [Authorize(Policy = "RequireOrgAdmin")]
        public async Task<IActionResult> UpdateMemberRole(
            [FromRoute] Guid orgId,
            [FromRoute] Guid userId,
            [FromBody] UpdateMemberRoleRequest request,
            CancellationToken cancellationToken)
        {
            var result = await _memberService.UpdateMemberRoleAsync(orgId, userId, request, cancellationToken);

            return result.IsSuccess
                ? NoContent()
                : result.ToProblem(_correlationIdProvider);
        }
    }
}
