using Microsoft.AspNetCore.Mvc;
using MeetingAssistant.Features.Identity.Services;
using MeetingAssistant.Shared.Abstractions;
using MeetingAssistant.Shared.Helpers;
using MeetingAssistant.Api.Infrastructure.Services;

namespace MeetingAssistant.Features.Identity.Endpoints
{
    [ApiController]
    [Route("api/[controller]")]
    public partial class AuthController(
        IAuthService authService,
        ICorrelationIdProvider correlationIdProvider) : ControllerBase
    {
        private readonly IAuthService _authService = authService;
        private readonly ICorrelationIdProvider _correlationIdProvider = correlationIdProvider;
    }
}