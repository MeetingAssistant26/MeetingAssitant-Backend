namespace MeetingAssistant.Contracts.Authentication
{
    public record AuthResponse
    (
        string Id,
        string? FirstName,
        string? LastName,
        string? Email,
        string? AvatarUrl,
        string? Token,
        int ExpiresIn,
        string RefreshToken,
        DateTime RefreshTokenExpiresOn
    );
  
}
