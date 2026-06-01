using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MeetingAssistant.Api.Shared
{
    public class StandardErrorResponse
    {
        public string? Type { get; set; }
        public string? Title { get; set; }
        public int Status { get; set; }
        public Dictionary<string, string[]>? Errors { get; set; }
        public string? CorrelationId { get; set; }

        [JsonExtensionData]
        public Dictionary<string, JsonElement>? Extensions { get; set; }
    }
}
