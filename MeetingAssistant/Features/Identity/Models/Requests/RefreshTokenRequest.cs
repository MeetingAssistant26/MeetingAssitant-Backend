namespace MeetingAssistant.Features.Identity.DTOs
{
    public record RefreshTokenRequest
    (
        string Token,
        string RefreshToken
    );
}
