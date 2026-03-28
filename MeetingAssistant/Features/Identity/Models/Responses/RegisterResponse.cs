namespace MeetingAssistant.Features.Identity.DTOs
{
    public record RegisterResponse(
        Guid UserId,
        string Email,
        string DisplayName
    );
}