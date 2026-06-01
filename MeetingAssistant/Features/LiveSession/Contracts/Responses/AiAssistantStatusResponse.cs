namespace MeetingAssistant.Features.LiveSession.Contracts.Responses
{
    public sealed record AiAssistantStatusResponse(
        bool Enabled,
        bool AgentIsConnected,
        string? DispatchId);
}
