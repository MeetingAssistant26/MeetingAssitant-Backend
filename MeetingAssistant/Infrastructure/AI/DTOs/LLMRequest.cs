using System.Text.Json.Serialization;

namespace MeetingAssistant.Infrastructure.AI.DTOs
{
    public sealed record LLMRequest
    {
        [JsonPropertyName("model")]
        public required string Model { get; init; }

        [JsonPropertyName("messages")]
        public required List<ChatMessage> Messages { get; init; }

        [JsonPropertyName("temperature")]
        public double? Temperature { get; init; }

        [JsonPropertyName("max_tokens")]
        public int? MaxTokens { get; init; }

        [JsonPropertyName("response_format")]
        public ResponseFormat? ResponseFormat { get; init; }
    }

    public sealed record ChatMessage
    {
        [JsonPropertyName("role")]
        public required string Role { get; init; }

        [JsonPropertyName("content")]
        public required string Content { get; init; }
    }

    public sealed record ResponseFormat
    {
        [JsonPropertyName("type")]
        public required string Type { get; init; }
    }
}
