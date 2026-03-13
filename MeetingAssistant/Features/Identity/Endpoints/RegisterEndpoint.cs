using Microsoft.AspNetCore.Mvc;
using MeetingAssistant.Shared.Abstractions;
using MeetingAssistant.Features.Identity.DTOs;

namespace MeetingAssistant.Features.Identity.Endpoints
{
    public partial class AuthController
    {
        [HttpPost("register")]
        public async Task<IActionResult> Register([FromBody] RegisterRequest registerRequest, CancellationToken cancellationToken)
        {
            if (!ModelState.IsValid)
            {
                return BadRequest(ModelState);
            }
            var result = await _authService.RegisterAsync(registerRequest, cancellationToken);

            return result.IsSuccess ? Ok()
                            : result.ToProblem(_correlationIdProvider);
        }
    }
}
