using MeetingAssistant.Api.Infrastructure.Services;
using MeetingAssistant.Features.Meetings.Services;
using MeetingAssistant.Features.Organizations.Infrastructure.Filters;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MeetingAssistant.Features.Meetings.Endpoints.Meeting
{
    [ApiController]
    [Route("api/organizations/{orgId:guid}/meetings")]
    [Authorize]
    [EnforceOrgAccess]
    public partial class MeetingController(
        IMeetingService meetingService,
        ICorrelationIdProvider correlationIdProvider) : ControllerBase
    {
        protected readonly IMeetingService _meetingService = meetingService;
        protected readonly ICorrelationIdProvider _correlationIdProvider = correlationIdProvider;
    }
}
