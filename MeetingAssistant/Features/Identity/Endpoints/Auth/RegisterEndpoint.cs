using Microsoft.AspNetCore.Mvc;
using MeetingAssistant.Shared.Abstractions;
using MeetingAssistant.Features.Identity.DTOs;

using Microsoft.AspNetCore.Authorization;
namespace MeetingAssistant.Features.Identity.Endpoints
{
    public partial class AuthController
    {
        [AllowAnonymous]
        [HttpPost("register")]
        public async Task<IActionResult> Register([FromBody] RegisterRequest registerRequest, CancellationToken cancellationToken)
        {
            var result = await _authService.RegisterAsync(registerRequest, cancellationToken);

            return result.IsSuccess ? Ok(result.Value)
                            : result.ToProblem(_correlationIdProvider);
        }
    }
}
