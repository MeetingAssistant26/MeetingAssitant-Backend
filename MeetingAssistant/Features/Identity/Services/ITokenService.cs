using MeetingAssistant.Features.Identity.Entites;

namespace MeetingAssistant.Features.Identity.Services
{
    public interface ITokenService
    {
        (string Token, int ExpiresIn) GenerateAccessToken(ApplicationUser user, IEnumerable<string> roles, IEnumerable<string> permissions);
        string GenerateRefreshToken();
        string HashToken(string token);
    }
}
