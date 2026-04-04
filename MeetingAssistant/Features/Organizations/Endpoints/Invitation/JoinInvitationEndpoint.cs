using System.Security.Claims;
using MeetingAssistant.Features.Organizations.Contracts.Requests;
using MeetingAssistant.Shared.Abstractions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MeetingAssistant.Features.Organizations.Endpoints.Invitation
{
    public partial class InvitationController
    {
        [HttpPost("/api/invitations/{token}/join")]
        [Authorize]
        public async Task<IActionResult> JoinInvitation(
            [FromRoute] string token,
            CancellationToken cancellationToken)
        {
            var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
            var result = await _invitationService.JoinInvitationAsync(token, userId, cancellationToken);

            return result.IsSuccess
                ? Ok(result.Value)
                : result.ToProblem(_correlationIdProvider);
        }
    }
}