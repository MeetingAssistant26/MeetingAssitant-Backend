namespace MeetingAssistant.Features.Identity.Models.Requests
{
    public record LoginRequest(
        string Email,
        string Password
        );
    
}

