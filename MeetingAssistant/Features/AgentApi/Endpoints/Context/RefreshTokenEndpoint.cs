using MeetingAssistant.Features.AgentApi.Models.Responses;
using MeetingAssistant.Shared.Abstractions;
using MeetingAssistant.Shared.Errors;
using Microsoft.AspNetCore.Mvc;

namespace MeetingAssistant.Features.AgentApi.Endpoints.Context
{
    public partial class AgentContextController
    {
        [HttpPost("refresh")]
        public async Task<IActionResult> RefreshToken(CancellationToken cancellationToken = default)
        {
            var authorization = Request.Headers.Authorization.ToString();
            const string bearerPrefix = "Bearer ";
            if (string.IsNullOrWhiteSpace(authorization)
                || !authorization.StartsWith(bearerPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return Result.Failure(AgentApiErrors.MissingClaims).ToProblem(_correlationIdProvider);
            }

            var token = authorization[bearerPrefix.Length..].Trim();
            if (string.IsNullOrWhiteSpace(token))
            {
                return Result.Failure(AgentApiErrors.MissingClaims).ToProblem(_correlationIdProvider);
            }

            var result = await _agentAuthService.RefreshTokenAsync(token, cancellationToken);

            return result.IsSuccess
                ? Ok(new AgentTokenResponse(result.Value))
                : result.ToProblem(_correlationIdProvider);
        }
    }
}
