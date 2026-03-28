using Microsoft.AspNetCore.Mvc;
using MeetingAssistant.Features.Identity.Services;

namespace MeetingAssistant.Features.Identity.Endpoints.Token
{
    [ApiController]
    [Route("api/auth/tokens")]
    public partial class TokenController(ITokenService tokenService) : ControllerBase
    {
        protected readonly ITokenService _tokenService = tokenService;
    }
}
