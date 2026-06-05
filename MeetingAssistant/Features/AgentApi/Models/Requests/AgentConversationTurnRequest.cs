namespace MeetingAssistant.Features.AgentApi.Models.Requests
{
    public sealed record AgentConversationTurnRequest(
        string Role,
        string Text);
}
