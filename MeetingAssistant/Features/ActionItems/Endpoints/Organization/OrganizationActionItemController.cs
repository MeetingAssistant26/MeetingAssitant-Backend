using MeetingAssistant.Api.Infrastructure.Services;
using MeetingAssistant.Features.ActionItems.Services;
using MeetingAssistant.Features.Organizations.Infrastructure.Filters;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MeetingAssistant.Features.ActionItems.Endpoints.Organization
{
    [ApiController]
    [Route("api/organizations/{orgId:guid}/action-items")]
    [Authorize(Policy = "RequireOrgAccess")]
    [EnforceOrgAccess]
    public partial class OrganizationActionItemController(
        IActionItemService actionItemService,
        ICorrelationIdProvider correlationIdProvider) : ControllerBase
    {
        protected readonly IActionItemService _actionItemService = actionItemService;
        protected readonly ICorrelationIdProvider _correlationIdProvider = correlationIdProvider;
    }
}
