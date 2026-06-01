namespace MeetingAssistant.Features.LiveSession.Infrastructure
{
    public sealed class LiveKitOptions
    {
        public string ApiKey { get; init; } = string.Empty;
        public string ApiSecret { get; init; } = string.Empty;
        public string ServerUrl { get; init; } = string.Empty;
        public string WebhookSecret { get; init; } = string.Empty;
        public string EgressHost { get; init; } = string.Empty;
        public string AgentName { get; init; } = "meeting-assistant";
        public bool AiAssistantDefaultEnabled { get; init; } = true;
    }
}
