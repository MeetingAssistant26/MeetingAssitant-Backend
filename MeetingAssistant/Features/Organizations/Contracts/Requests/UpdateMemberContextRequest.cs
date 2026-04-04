namespace MeetingAssistant.Features.Organizations.Contracts.Requests
{
    public record UpdateMemberContextRequest(
        string? JobRole,
        string? Context
    );
}