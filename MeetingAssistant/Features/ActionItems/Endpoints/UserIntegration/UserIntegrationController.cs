using MeetingAssistant.Api.Infrastructure.Services;
using MeetingAssistant.Features.ActionItems.Models.Enums;
using MeetingAssistant.Features.ActionItems.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MeetingAssistant.Features.ActionItems.Endpoints.UserIntegration
{
    [ApiController]
    [Route("api/users/me/integrations")]
    [Authorize]
    public partial class UserIntegrationController(
        IUserIntegrationService userIntegrationService,
        ITenantProvider tenantProvider,
        ICorrelationIdProvider correlationIdProvider) : ControllerBase
    {
        protected readonly IUserIntegrationService _userIntegrationService = userIntegrationService;
        protected readonly ITenantProvider _tenantProvider = tenantProvider;
        protected readonly ICorrelationIdProvider _correlationIdProvider = correlationIdProvider;
    }
}
