using Microsoft.AspNetCore.Mvc;
using MeetingAssistant.Features.Identity.Services;

namespace MeetingAssistant.Features.Identity.Endpoints.Profile
{
    [ApiController]
    [Route("api/auth/profile")]
    public partial class ProfileController(IProfileService profileService) : ControllerBase
    {
        protected readonly IProfileService _profileService = profileService;
    }
}
