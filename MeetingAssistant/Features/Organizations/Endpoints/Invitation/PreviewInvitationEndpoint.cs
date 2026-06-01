using MeetingAssistant.Shared.Abstractions;
using MeetingAssistant.Api.Infrastructure.Services;
using MeetingAssistant.Features.Organizations.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MeetingAssistant.Features.Organizations.Endpoints.Invitation
{
    [ApiController]
    [Route("api/invitations")]
    public class PublicInvitationPreviewController(
        IInvitationService invitationService,
        ICorrelationIdProvider correlationIdProvider) : ControllerBase
    {
        [HttpGet("{token}/preview")]
        [AllowAnonymous]
        public async Task<IActionResult> PreviewInvitation(
            [FromRoute] string token,
            CancellationToken cancellationToken)
        {
            var result = await invitationService.PreviewInvitationAsync(token, cancellationToken);

            return result.IsSuccess
                ? Ok(result.Value)
                : result.ToProblem(correlationIdProvider);
        }
    }
}
