using System.Security.Claims;
using MeetingAssistant.Features.Organizations.Contracts.Requests;
using MeetingAssistant.Features.Organizations.Contracts.Responses;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MeetingAssistant.Shared.Abstractions;

namespace MeetingAssistant.Features.Organizations.Endpoints.Invitation
{
    public partial class InvitationController
    {
        [HttpPost]
        [Authorize(Policy = "RequireOrgAdmin")]
        public async Task<IActionResult> CreateInvitation(
            [FromRoute] Guid orgId,
            [FromBody] CreateInvitationRequest request,
            CancellationToken cancellationToken)
        {
            var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
            var result = await _invitationService.CreateInvitationAsync(orgId, userId, request, cancellationToken);

            return result.IsSuccess
                ? Ok(result.Value)
                : result.ToProblem(_correlationIdProvider);
        }
    }
}
