using Microsoft.AspNetCore.Mvc;
using MeetingAssistant.Shared.Abstractions;
using MeetingAssistant.Features.Identity.Models.Requests;
using Microsoft.AspNetCore.Authorization;

namespace MeetingAssistant.Features.Identity.Endpoints
{
    public partial class AuthController
    {
        [AllowAnonymous]
        [HttpPost("login")]
        public async Task<IActionResult> Login([FromBody] LoginRequest loginRequest, CancellationToken cancellationToken)
        {
            var authresult = await _authService.LoginAsync(loginRequest, cancellationToken);

            return authresult.IsSuccess ? Ok(authresult.Value)
                           : authresult.ToProblem(_correlationIdProvider);      
        }
    }
}
