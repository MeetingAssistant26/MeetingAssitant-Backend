using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using MeetingAssistant.Features.Identity.Entites;
using MeetingAssistant.Api.Infrastructure.Configuration;

namespace MeetingAssistant.Features.Identity.Services
{
    public class TokenService(IOptions<JwtSettings> jwtSettingsOptions) : ITokenService
    {
        private readonly JwtSettings _jwtSettings = jwtSettingsOptions.Value;

        public (string Token, int ExpiresIn) GenerateAccessToken(ApplicationUser User, Guid? organizationId = null, string? orgRole = null)
        {
            var claims = new List<Claim> {
                new(JwtRegisteredClaimNames.Sub, User.Id.ToString()),
                new(JwtRegisteredClaimNames.Email, User.Email!),
                new(JwtRegisteredClaimNames.Name, User.DisplayName ?? string.Empty),
                new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            };

            if (organizationId.HasValue)
            {
                claims.Add(new Claim("organizationId", organizationId.Value.ToString()));
                claims.Add(new Claim("org_role", orgRole!));
            }

            var symmetricSecurityKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_jwtSettings.SigningKey!));
            var signingCredentials = new SigningCredentials(symmetricSecurityKey, SecurityAlgorithms.HmacSha256);

            var token = new JwtSecurityToken(
                issuer: _jwtSettings.Issuer,
                audience: _jwtSettings.Audience,
                claims: claims,
                expires: DateTime.UtcNow.AddMinutes(_jwtSettings.TokenExpiryMinutes),
                signingCredentials: signingCredentials
            );

            return (new JwtSecurityTokenHandler().WriteToken(token), _jwtSettings.TokenExpiryMinutes);
        }

        public string GenerateRefreshToken()
        {
            var randomNumber = new byte[32];
            using var rng = RandomNumberGenerator.Create();
            rng.GetBytes(randomNumber);
            return Convert.ToBase64String(randomNumber);
        }

        public string HashToken(string token)
        {
            var tokenBytes = Encoding.UTF8.GetBytes(token);
            var hashBytes = SHA256.HashData(tokenBytes);
            return Convert.ToBase64String(hashBytes);
        }

    }
}
