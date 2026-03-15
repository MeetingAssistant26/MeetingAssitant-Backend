using Microsoft.AspNetCore.Mvc;
using MeetingAssistant.Features.Identity.Services;

namespace MeetingAssistant.Features.Identity.Endpoints.Auth
{
    [ApiController]
    [Route("api/auth")]
    public partial class AuthController(IAuthService authService, ITokenService tokenService) : ControllerBase
    {
        protected readonly IAuthService _authService = authService;
        protected readonly ITokenService _tokenService = tokenService;
    }
}
