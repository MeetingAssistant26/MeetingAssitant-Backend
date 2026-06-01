using MeetingAssistant.Api.Infrastructure.Services;
using MeetingAssistant.Features.LiveSession.Services;
using MeetingAssistant.Features.Organizations.Infrastructure.Filters;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MeetingAssistant.Features.LiveSession.Endpoints.Artifacts
{
    [ApiController]
    [Route("api/organizations/{orgId:guid}/meetings/{meetingId:guid}")]
    [Authorize]
    [EnforceOrgAccess]
    public partial class MeetingArtifactController(
        IMeetingArtifactService meetingArtifactService,
        ICorrelationIdProvider correlationIdProvider) : ControllerBase
    {
        protected readonly IMeetingArtifactService _meetingArtifactService = meetingArtifactService;
        protected readonly ICorrelationIdProvider _correlationIdProvider = correlationIdProvider;
    }
}
