namespace MeetingAssistant.Features.Identity.Models.Responses
{
    public record AuthResponse
    (
        string Id,
        string? DisplayName,
        string? Email,
        string? AvatarUrl,
        string? Token,
        int ExpiresIn,
        string RefreshToken,
        DateTime RefreshTokenExpiresOn
    );
}
