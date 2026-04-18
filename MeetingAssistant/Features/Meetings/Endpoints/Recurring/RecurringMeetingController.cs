using MeetingAssistant.Api.Infrastructure.Services;
using MeetingAssistant.Features.Meetings.Services;
using MeetingAssistant.Features.Organizations.Infrastructure.Filters;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MeetingAssistant.Features.Meetings.Endpoints.Recurring
{
    [ApiController]
    [Route("api/organizations/{orgId:guid}/meetings/recurring")]
    [Authorize]
    [EnforceOrgAccess]
    public partial class RecurringMeetingController(
        IRecurrenceService recurrenceService,
        ICorrelationIdProvider correlationIdProvider) : ControllerBase
    {
        private readonly IRecurrenceService _recurrenceService = recurrenceService;
        private readonly ICorrelationIdProvider _correlationIdProvider = correlationIdProvider;
    }
}