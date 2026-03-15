namespace MeetingAssistant.Features.Identity.DTOs
{
    public record LoginRequest(
        string Email,
        string Password
        );
    
}
