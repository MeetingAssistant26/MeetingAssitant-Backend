using MeetingAssistant.Api.Infrastructure.Services;
using MeetingAssistant.Features.Meetings.Services;
using MeetingAssistant.Features.Organizations.Infrastructure.Filters;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MeetingAssistant.Features.Meetings.Endpoints.Calendar
{
    [ApiController]
    [Route("api/organizations/{orgId:guid}/calendar")]
    [Authorize]
    [EnforceOrgAccess]
    public partial class CalendarController(
        ICalendarService calendarService,
        ICorrelationIdProvider correlationIdProvider) : ControllerBase
    {
        private readonly ICalendarService _calendarService = calendarService;
        private readonly ICorrelationIdProvider _correlationIdProvider = correlationIdProvider;
    }
}