using MeetingAssistant.Entities;

namespace MeetingAssistant.Authentication
{
    public interface IJwtProvider
    {
        (string token, int expiresIn) GenerateJwtToken(ApplicationUser applicationUser, IEnumerable<string> roles, IEnumerable<string> permissions);
       
        string? validateJwtToken(string token);
    }
}
