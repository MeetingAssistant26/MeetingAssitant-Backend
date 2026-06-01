using MeetingAssistant.Api.Infrastructure.Services;
using MeetingAssistant.Features.LiveSession.Services;
using MeetingAssistant.Features.Organizations.Infrastructure.Filters;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MeetingAssistant.Features.LiveSession.Endpoints.AiAssistant
{
    [ApiController]
    [Route("api/organizations/{orgId:guid}/meetings/{meetingId:guid}/ai-assistant")]
    [Authorize]
    [EnforceOrgAccess]
    public partial class AiAssistantController(
        IAiAssistantDispatchService aiAssistantDispatchService,
        ICorrelationIdProvider correlationIdProvider) : ControllerBase
    {
        protected readonly IAiAssistantDispatchService _aiAssistantDispatchService = aiAssistantDispatchService;
        protected readonly ICorrelationIdProvider _correlationIdProvider = correlationIdProvider;
    }
}
