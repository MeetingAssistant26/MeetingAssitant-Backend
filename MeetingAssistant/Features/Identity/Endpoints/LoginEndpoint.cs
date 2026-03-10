using Microsoft.AspNetCore.Mvc;
using MeetingAssistant.Shared.Abstractions;
using MeetingAssistant.Features.Identity.DTOs;

namespace MeetingAssistant.Features.Identity.Endpoints
{
    public partial class AuthController
    {
        [HttpPost]
        public async Task<IActionResult> Login(LoginRequest loginRequest, CancellationToken cancellationToken)
        {
            var authresult = await _authService.GetTokenAsync(loginRequest.Email, loginRequest.Password, cancellationToken);

            return authresult.IsSuccess ? Ok(authresult.Value)
                           : authresult.ToProblem();
        }
    }
}
