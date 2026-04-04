namespace MeetingAssistant.Features.Organizations.Contracts.Responses
{
    public record CreateInvitationResponse(
        Guid Id,
        string Token,
        DateTime ExpiresAtUtc
    );
}