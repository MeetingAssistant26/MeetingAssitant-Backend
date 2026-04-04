using System.Security.Claims;
using MeetingAssistant.Features.Organizations.Contracts.Requests;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MeetingAssistant.Shared.Abstractions;

namespace MeetingAssistant.Features.Organizations.Endpoints.Member
{
    public partial class MemberController
    {
        [HttpPut("{userId:guid}/context")]
        [Authorize(Policy = "RequireOrgMember")]
        public async Task<IActionResult> UpdateMemberContext(
            [FromRoute] Guid orgId,
            [FromRoute] Guid userId,
            [FromBody] UpdateMemberContextRequest request,
            CancellationToken cancellationToken)
        {
            var currentUserId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
            var result = await _memberService.UpdateMemberContextAsync(orgId, userId, request, currentUserId, cancellationToken);

            return result.IsSuccess
                ? NoContent()
                : result.ToProblem(_correlationIdProvider);
        }
    }
}