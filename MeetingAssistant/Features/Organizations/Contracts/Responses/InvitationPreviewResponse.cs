namespace MeetingAssistant.Features.Organizations.Contracts.Responses
{
    public record InvitationPreviewResponse(
        string OrganizationName,
        string OrganizationSlug,
        DateTime ExpiresAtUtc
    );
}
