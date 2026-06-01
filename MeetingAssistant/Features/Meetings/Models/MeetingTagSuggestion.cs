using MeetingAssistant.Api.Shared;
using MeetingAssistant.Features.Organizations.Models;
using MeetingAssistant.Shared.Abstractions;

namespace MeetingAssistant.Features.Meetings.Models
{
    public class MeetingTagSuggestion : BaseEntity, IHasOrganizationId
    {
        public Guid OrganizationId { get; set; }

        public Guid MeetingId { get; set; }
        public Meeting Meeting { get; set; } = default!;

        public Guid MeetingTagId { get; set; }
        public MeetingTag MeetingTag { get; set; } = default!;

        public string TagNameSnapshot { get; set; } = string.Empty;

        public string? TagColorSnapshot { get; set; }

        public decimal? Confidence { get; set; }

        public string? Reason { get; set; }

        public MeetingTagSuggestionStatus Status { get; set; } = MeetingTagSuggestionStatus.PendingReview;

        public string LlmModel { get; set; } = string.Empty;

        public Guid? TranscriptId { get; set; }

        public Guid? SummaryId { get; set; }

        public DateTime SuggestedAtUtc { get; set; }

        public string MetadataJson { get; set; } = "{}";
    }
}
