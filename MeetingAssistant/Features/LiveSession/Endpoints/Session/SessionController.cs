using MeetingAssistant.Api.Infrastructure.Services;
using MeetingAssistant.Features.LiveSession.Services;
using MeetingAssistant.Features.Organizations.Infrastructure.Filters;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MeetingAssistant.Features.LiveSession.Endpoints.Session
{
    [ApiController]
    [Route("api/organizations/{orgId:guid}/meetings/{meetingId:guid}/session")]
    [Authorize]
    [EnforceOrgAccess]
    public partial class SessionController(
        ISessionService sessionService,
        ICorrelationIdProvider correlationIdProvider) : ControllerBase
    {
        protected readonly ISessionService _sessionService = sessionService;
        protected readonly ICorrelationIdProvider _correlationIdProvider = correlationIdProvider;
    }
}
