using MeetingAssistant.Api.Infrastructure.Services;
using MeetingAssistant.Features.ActionItems.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MeetingAssistant.Features.ActionItems.Endpoints.Review
{
    [ApiController]
    [Route("api/organizations/{organizationId:guid}/meetings/{meetingId:guid}/action-items")]
    [Authorize]
    public partial class ActionItemReviewController(
        IActionItemService actionItemService,
        ITenantProvider tenantProvider,
        ICorrelationIdProvider correlationIdProvider) : ControllerBase
    {
        protected readonly IActionItemService _actionItemService = actionItemService;
        protected readonly ITenantProvider _tenantProvider = tenantProvider;
        protected readonly ICorrelationIdProvider _correlationIdProvider = correlationIdProvider;
    }
}
