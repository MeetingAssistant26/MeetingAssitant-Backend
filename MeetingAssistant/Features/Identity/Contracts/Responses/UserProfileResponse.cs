namespace MeetingAssistant.Features.Identity.Models.Responses
{
    public record UserProfileResponse(
        Guid UserId,
        string Email,
        string DisplayName,
        string? ProfileAvatarUrl
    );
}
