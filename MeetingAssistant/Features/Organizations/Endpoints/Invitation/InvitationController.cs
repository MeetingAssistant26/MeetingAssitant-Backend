using MeetingAssistant.Api.Infrastructure.Services;
using MeetingAssistant.Features.Organizations.Services;
using Microsoft.AspNetCore.Mvc;

namespace MeetingAssistant.Features.Organizations.Endpoints.Invitation
{
    [ApiController]
    [Route("api/organizations/{orgId:guid}/invitations")]
    public partial class InvitationController(
        IInvitationService invitationService,
        ICorrelationIdProvider correlationIdProvider) : ControllerBase
    {
        protected readonly IInvitationService _invitationService = invitationService;
        protected readonly ICorrelationIdProvider _correlationIdProvider = correlationIdProvider;
    }
}