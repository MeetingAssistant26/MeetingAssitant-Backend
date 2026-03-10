using Microsoft.AspNetCore.Mvc;
using MeetingAssistant.Features.Identity.Services;

namespace MeetingAssistant.Features.Identity.Endpoints
{
    [Route("/[controller]")]
    [ApiController]
    public partial class AuthController(IAuthService authService) : ControllerBase
    {
        private readonly IAuthService _authService = authService;
    }
}
