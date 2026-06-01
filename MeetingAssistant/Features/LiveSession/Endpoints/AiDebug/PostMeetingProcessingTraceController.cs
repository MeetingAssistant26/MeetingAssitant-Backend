using MeetingAssistant.Api.Infrastructure.Services;
using MeetingAssistant.Features.LiveSession.Services.PostProcessing;
using MeetingAssistant.Features.Organizations.Infrastructure.Filters;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MeetingAssistant.Features.LiveSession.Endpoints.AiDebug
{
    [ApiController]
    [Route("api/organizations/{orgId:guid}/meetings/{meetingId:guid}/ai-debug/post-processing-traces")]
    [Authorize(Policy = "RequireOrgAdmin")]
    [EnforceOrgAccess]
    public partial class PostMeetingProcessingTraceController(
        IPostMeetingProcessingTraceService postMeetingProcessingTraceService,
        ICorrelationIdProvider correlationIdProvider) : ControllerBase
    {
        protected readonly IPostMeetingProcessingTraceService _postMeetingProcessingTraceService = postMeetingProcessingTraceService;
        protected readonly ICorrelationIdProvider _correlationIdProvider = correlationIdProvider;
    }
}
