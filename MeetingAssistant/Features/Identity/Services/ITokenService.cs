using MeetingAssistant.Features.Identity.Entites;

namespace MeetingAssistant.Features.Identity.Services
{
    public interface ITokenService
    {
        (string Token, int ExpiresIn) GenerateAccessToken(ApplicationUser user, Guid? organizationId = null, string? orgRole = null);
        string GenerateRefreshToken();
        string HashToken(string token);
    }
}
