namespace MeetingAssistant.Features.LiveSession.Infrastructure
{
    public sealed class StorageOptions
    {
        public string Endpoint { get; init; } = string.Empty;
        public string AccessKey { get; init; } = string.Empty;
        public string SecretKey { get; init; } = string.Empty;
        public string Bucket { get; init; } = string.Empty;
    }
}
