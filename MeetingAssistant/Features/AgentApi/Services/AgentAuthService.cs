using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using MeetingAssistant.Api.Infrastructure.Configuration;
using MeetingAssistant.Features.Meetings.Models;
using MeetingAssistant.Infrastructure.Persistence.DbContext;
using MeetingAssistant.Shared.Abstractions;
using MeetingAssistant.Shared.Errors;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace MeetingAssistant.Features.AgentApi.Services
{
    public class AgentAuthService(
        IOptions<AgentJwtSettings> settingsOptions,
        ApplicationDbContext dbContext) : IAgentAuthService
    {
        private readonly AgentJwtSettings _settings = settingsOptions.Value;
        private readonly ApplicationDbContext _dbContext = dbContext;

        public Task<Result<string>> MintTokenAsync(
            Guid organizationId,
            Guid meetingId,
            TimeSpan lifetime,
            CancellationToken cancellationToken = default)
        {
            if (lifetime <= TimeSpan.Zero)
            {
                return Task.FromResult(Result.Failure<string>(AgentApiErrors.InvalidTokenLifetime));
            }

            var signingKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_settings.SigningKey!));
            var signingCredentials = new SigningCredentials(signingKey, SecurityAlgorithms.HmacSha256);

            var claims = new List<Claim>
            {
                new(JwtRegisteredClaimNames.Sub, "agent"),
                new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
                new(AgentAuthenticationDefaults.AgentClaim, "true"),
                new("organizationId", organizationId.ToString()),
                new("meetingId", meetingId.ToString())
            };

            var token = new JwtSecurityToken(
                issuer: _settings.Issuer,
                audience: _settings.Audience,
                claims: claims,
                expires: DateTime.UtcNow.Add(lifetime),
                signingCredentials: signingCredentials);

            return Task.FromResult(Result.Success(new JwtSecurityTokenHandler().WriteToken(token)));
        }

        public async Task<Result<string>> RefreshTokenAsync(
            string currentToken,
            CancellationToken cancellationToken = default)
        {
            // Read claims from the token without enforcing lifetime validation,
            // so that expired tokens can be refreshed while the meeting is active.
            var claimsResult = ReadAgentClaims(currentToken);
            if (claimsResult.IsFailure)
            {
                return Result.Failure<string>(claimsResult.Error);
            }

            var (organizationId, meetingId) = claimsResult.Value;

            var meetingStatus = await _dbContext.Meetings
                .Where(m => m.Id == meetingId && m.OrganizationId == organizationId)
                .Select(m => m.Status)
                .FirstOrDefaultAsync(cancellationToken);

            if (meetingStatus != MeetingStatus.InProgress)
            {
                return Result.Failure<string>(AgentApiErrors.TokenRefreshDenied);
            }

            var lifetime = TimeSpan.FromMinutes(Math.Max(_settings.TokenExpiryMinutes, 1));
            return await MintTokenAsync(organizationId, meetingId, lifetime, cancellationToken);
        }

        private Result<(Guid OrganizationId, Guid MeetingId)> ReadAgentClaims(string token)
        {
            try
            {
                var handler = new JwtSecurityTokenHandler();
                var jwt = handler.ReadJwtToken(token);

                var signingKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_settings.SigningKey!));
                var validationParameters = new TokenValidationParameters
                {
                    ValidateIssuerSigningKey = true,
                    ValidateAudience = true,
                    ValidateIssuer = true,
                    ValidateLifetime = false, // Allow expired tokens to be refreshed
                    ClockSkew = TimeSpan.Zero,
                    ValidIssuer = _settings.Issuer,
                    ValidAudience = _settings.Audience,
                    IssuerSigningKey = signingKey
                };

                var principal = handler.ValidateToken(token, validationParameters, out _);
                var isAgent = string.Equals(
                    principal.FindFirstValue(AgentAuthenticationDefaults.AgentClaim),
                    "true",
                    StringComparison.OrdinalIgnoreCase);

                if (!isAgent)
                {
                    return Result.Failure<(Guid, Guid)>(AgentApiErrors.AccessDenied);
                }

                var orgClaim = principal.FindFirstValue("organizationId");
                var meetingClaim = principal.FindFirstValue("meetingId");
                if (!Guid.TryParse(orgClaim, out var organizationId) || !Guid.TryParse(meetingClaim, out var meetingId))
                {
                    return Result.Failure<(Guid, Guid)>(AgentApiErrors.MissingClaims);
                }

                return Result.Success((organizationId, meetingId));
            }
            catch
            {
                return Result.Failure<(Guid, Guid)>(AgentApiErrors.AccessDenied);
            }
        }

        private Result<ClaimsPrincipal> ValidateToken(string token)
        {
            try
            {
                var parameters = new TokenValidationParameters
                {
                    ValidateIssuerSigningKey = true,
                    ValidateAudience = true,
                    ValidateIssuer = true,
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.Zero,
                    ValidIssuer = _settings.Issuer,
                    ValidAudience = _settings.Audience,
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_settings.SigningKey!))
                };

                var principal = new JwtSecurityTokenHandler().ValidateToken(token, parameters, out _);
                var isAgent = string.Equals(
                    principal.FindFirstValue(AgentAuthenticationDefaults.AgentClaim),
                    "true",
                    StringComparison.OrdinalIgnoreCase);

                return isAgent
                    ? Result.Success(principal)
                    : Result.Failure<ClaimsPrincipal>(AgentApiErrors.AccessDenied);
            }
            catch
            {
                return Result.Failure<ClaimsPrincipal>(AgentApiErrors.AccessDenied);
            }
        }
    }
}
