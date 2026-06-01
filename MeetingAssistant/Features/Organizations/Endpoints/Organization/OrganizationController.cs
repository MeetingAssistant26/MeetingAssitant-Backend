using MeetingAssistant.Api.Infrastructure.Services;
using MeetingAssistant.Features.Organizations.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MeetingAssistant.Features.Organizations.Endpoints.Organization
{
    [ApiController]
    [Route("api/organizations")]
    [Authorize]
    public partial class OrganizationController(
        IOrganizationService organizationService,
        ICorrelationIdProvider correlationIdProvider) : ControllerBase
    {
        protected readonly IOrganizationService _organizationService = organizationService;
        protected readonly ICorrelationIdProvider _correlationIdProvider = correlationIdProvider;
    }
}
