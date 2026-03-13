using Microsoft.AspNetCore.Mvc;
using MeetingAssistant.Features.Identity.Services;
using MeetingAssistant.Api.Infrastructure.Services;

namespace MeetingAssistant.Features.Identity.Endpoints
{
    [Route("/[controller]")]
    [ApiController]
    public partial class AuthController(
        IAuthService authService,
        ICorrelationIdProvider correlationIdProvider) : ControllerBase
    {
        private readonly IAuthService _authService = authService;
        private readonly ICorrelationIdProvider _correlationIdProvider = correlationIdProvider;
    }
}
