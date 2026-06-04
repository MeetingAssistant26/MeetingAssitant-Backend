using MeetingAssistant.Api.Shared;
using MeetingAssistant.Features.Meetings.Models;
using MeetingAssistant.Shared.Abstractions;

namespace MeetingAssistant.Features.LiveSession.Models
{
    public class MeetingSummary : BaseEntity, IHasOrganizationId
    {
        public Guid MeetingId { get; set; }
        public Meeting Meeting { get; set; } = default!;

        public Guid OrganizationId { get; set; }

        public string SummaryText { get; set; } = string.Empty;

        public string LlmModel { get; set; } = string.Empty;

        public int? PromptTokens { get; set; }

        public int? CompletionTokens { get; set; }

        public DateTime GeneratedAtUtc { get; set; }

        public Guid? SourceTranscriptId { get; set; }

        public string? SourceTranscriptHash { get; set; }

        public int? SourceTranscriptRevision { get; set; }

        public DateTime? SourceTranscriptGeneratedAtUtc { get; set; }
    }
}
