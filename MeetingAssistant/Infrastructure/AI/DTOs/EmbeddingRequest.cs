using System.Text.Json.Serialization;

namespace MeetingAssistant.Infrastructure.AI.DTOs
{
    public sealed record EmbeddingRequest
    {
        [JsonPropertyName("model")]
        public required string Model { get; init; }

        [JsonPropertyName("input")]
        public required object Input { get; init; }
    }
}
