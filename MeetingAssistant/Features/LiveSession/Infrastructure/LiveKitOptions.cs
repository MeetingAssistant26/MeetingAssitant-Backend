namespace MeetingAssistant.Features.LiveSession.Infrastructure
{
    public sealed class LiveKitOptions
    {
        public string ApiKey { get; init; } = string.Empty;
        public string ApiSecret { get; init; } = string.Empty;
        public string ServerUrl { get; init; } = string.Empty;
        public string WebhookSecret { get; init; } = string.Empty;
    }
}
