using MeetingAssistant.Api.Infrastructure.Services;
using MeetingAssistant.Features.Organizations.Infrastructure.Filters;
using MeetingAssistant.Features.Organizations.Services;
using Microsoft.AspNetCore.Mvc;

namespace MeetingAssistant.Features.Organizations.Endpoints.MeetingTag
{
    [ApiController]
    [Route("api/organizations/{orgId:guid}/meeting-tags")]
    [EnforceOrgAccess]
    public partial class MeetingTagController(
        IMeetingTagService meetingTagService,
        ICorrelationIdProvider correlationIdProvider) : ControllerBase
    {
        protected readonly IMeetingTagService _meetingTagService = meetingTagService;
        protected readonly ICorrelationIdProvider _correlationIdProvider = correlationIdProvider;
    }
}
