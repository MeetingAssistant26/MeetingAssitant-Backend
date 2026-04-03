namespace MeetingAssistant.Features.Organizations.Contracts.Responses
{
    public record MemberResponse(
        Guid UserId,
        string Email,
        string DisplayName,
        string OrgRole,
        string? JobRole,
        string? Context,
        string? ContextStatus,
        bool IsEnabled
    );
}