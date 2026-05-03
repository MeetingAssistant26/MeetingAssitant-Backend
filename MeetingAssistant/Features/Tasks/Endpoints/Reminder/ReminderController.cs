using MeetingAssistant.Api.Infrastructure.Services;
using MeetingAssistant.Features.Tasks.Services;
using MeetingAssistant.Features.Organizations.Infrastructure.Filters;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MeetingAssistant.Features.Tasks.Endpoints.Reminder
{
    [ApiController]
    [Route("api/me/reminders")]
    [Authorize]
    public partial class ReminderController(
        IReminderService reminderService,
        ITenantProvider tenantProvider,
        ICorrelationIdProvider correlationIdProvider) : ControllerBase
    {
        protected readonly IReminderService _reminderService = reminderService;
        protected readonly ITenantProvider _tenantProvider = tenantProvider;
        protected readonly ICorrelationIdProvider _correlationIdProvider = correlationIdProvider;
    }
}
