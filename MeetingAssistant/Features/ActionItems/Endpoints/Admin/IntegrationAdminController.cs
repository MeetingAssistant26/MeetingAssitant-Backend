using MeetingAssistant.Api.Infrastructure.Services;
using MeetingAssistant.Features.ActionItems.Models.Enums;
using MeetingAssistant.Features.ActionItems.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MeetingAssistant.Features.ActionItems.Endpoints.Admin
{
    [ApiController]
    [Route("api/organizations/{organizationId:guid}/integrations")]
    [Authorize(Policy = "RequireOrgAdmin")]
    public partial class IntegrationAdminController(
        IIntegrationAdminService integrationAdminService,
        ITenantProvider tenantProvider,
        ICorrelationIdProvider correlationIdProvider) : ControllerBase
    {
        protected readonly IIntegrationAdminService _integrationAdminService = integrationAdminService;
        protected readonly ITenantProvider _tenantProvider = tenantProvider;
        protected readonly ICorrelationIdProvider _correlationIdProvider = correlationIdProvider;
    }
}
