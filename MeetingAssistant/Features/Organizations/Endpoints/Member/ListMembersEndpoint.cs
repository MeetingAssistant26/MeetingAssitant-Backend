using MeetingAssistant.Shared.Abstractions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MeetingAssistant.Features.Organizations.Endpoints.Member
{
    public partial class MemberController
    {
        [HttpGet]
        [Authorize(Policy = "RequireOrgAccess")]
        public async Task<IActionResult> ListMembers(
            [FromRoute] Guid orgId,
            CancellationToken cancellationToken)
        {
            var result = await _memberService.ListMembersAsync(orgId, cancellationToken);

            return result.IsSuccess
                ? Ok(result.Value)
                : result.ToProblem(_correlationIdProvider);
        }
    }
}