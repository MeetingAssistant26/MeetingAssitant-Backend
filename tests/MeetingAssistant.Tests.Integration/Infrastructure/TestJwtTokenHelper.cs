using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace MeetingAssistant.Tests.Integration.Infrastructure;

public static class TestJwtTokenHelper
{
    public const string TestSigningKey = "TestSigningKeyForIntegrationTests_MustBeAtLeast32Chars!!";
    public const string TestIssuer = "MeetingAssistant";
    public const string TestAudience = "MeetingAssistantClient";
    public const string TestAgentSigningKey = "AgentSigningKeyForIntegrationTests_MustBeAtLeast32Chars!!";
    public const string TestAgentIssuer = "MeetingAssistant";
    public const string TestAgentAudience = "MeetingAssistantAgent";

    public static string GenerateToken(Guid userId, Guid organizationId, string orgRole = "Admin", string? displayName = null)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(TestSigningKey));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, userId.ToString()),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            new(JwtRegisteredClaimNames.Email, $"test-{userId:N}@test.com"),
            new(JwtRegisteredClaimNames.Name, displayName ?? "Test User"),
            new("organizationId", organizationId.ToString()),
            new("org_role", orgRole)
        };

        var token = new JwtSecurityToken(
            issuer: TestIssuer,
            audience: TestAudience,
            claims: claims,
            expires: DateTime.UtcNow.AddHours(1),
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public static string GenerateAgentToken(Guid organizationId, Guid meetingId)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(TestAgentSigningKey));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, "agent"),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            new("agent", "true"),
            new("organizationId", organizationId.ToString()),
            new("meetingId", meetingId.ToString())
        };

        var token = new JwtSecurityToken(
            issuer: TestAgentIssuer,
            audience: TestAgentAudience,
            claims: claims,
            expires: DateTime.UtcNow.AddHours(1),
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
