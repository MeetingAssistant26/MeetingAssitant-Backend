namespace MeetingAssistant.Features.Organizations.Contracts.Requests
{
    public record CreateInvitationRequest(
        List<string> EmailWhitelist
    );
}