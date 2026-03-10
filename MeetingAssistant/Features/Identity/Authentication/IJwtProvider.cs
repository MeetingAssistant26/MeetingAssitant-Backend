using MeetingAssistant.Features.Identity.Entites;

namespace MeetingAssistant.Features.Authentication.Authentication
{
    public interface IJwtProvider
    {
        (string token, int expiresIn) GenerateJwtToken(ApplicationUser applicationUser, IEnumerable<string> roles, IEnumerable<string> permissions);
       
        string? validateJwtToken(string token);
    }
}
