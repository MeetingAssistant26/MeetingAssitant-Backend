namespace MeetingAssistant.Features.LiveSession.Infrastructure
{
    public class OpenAiCompatibleOptions
    {
        public ProviderConfig Stt { get; set; } = new();

        public ProviderConfig Llm { get; set; } = new();

        public class ProviderConfig
        {
            public string BaseUrl { get; set; } = string.Empty;

            public string ApiKey { get; set; } = string.Empty;

            public string Model { get; set; } = string.Empty;
        }
    }
}
