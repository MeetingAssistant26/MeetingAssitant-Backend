using MeetingAssistant.Api.Infrastructure.Services;
using MeetingAssistant.Features.LiveSession.Services;
using MeetingAssistant.Features.Organizations.Infrastructure.Filters;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MeetingAssistant.Features.LiveSession.Endpoints.AiDebug
{
    [ApiController]
    [Route("api/organizations/{orgId:guid}/meetings/{meetingId:guid}/ai-debug/traces")]
    [Authorize]
    [EnforceOrgAccess]
    public partial class AiDebugTraceController(
        IAiDebugTraceService aiDebugTraceService,
        ICorrelationIdProvider correlationIdProvider) : ControllerBase
    {
        protected readonly IAiDebugTraceService _aiDebugTraceService = aiDebugTraceService;
        protected readonly ICorrelationIdProvider _correlationIdProvider = correlationIdProvider;
    }
}
