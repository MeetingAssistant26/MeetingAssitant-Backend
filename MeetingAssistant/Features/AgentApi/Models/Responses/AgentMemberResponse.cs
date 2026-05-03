namespace MeetingAssistant.Features.AgentApi.Models.Responses
{
    public sealed record AgentMemberResponse(
        Guid UserId,
        string DisplayName,
        string? JobRole,
        string? Context);
}
