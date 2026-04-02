using Microsoft.AspNetCore.Mvc;
using MeetingAssistant.Features.Identity.Services;
using MeetingAssistant.Shared.Abstractions;
using MeetingAssistant.Api.Infrastructure.Services;

namespace MeetingAssistant.Features.Identity.Endpoints.Profile
{
    [ApiController]
    [Route("api/auth/profile")]
    public partial class ProfileController(IProfileService profileService, ICorrelationIdProvider correlationIdProvider) : ControllerBase
    {
        protected readonly IProfileService _profileService = profileService;
        protected readonly ICorrelationIdProvider _correlationIdProvider = correlationIdProvider;
    }
}
