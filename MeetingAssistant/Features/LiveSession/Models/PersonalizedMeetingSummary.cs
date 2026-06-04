using MeetingAssistant.Api.Shared;
using MeetingAssistant.Features.Identity.Entites;
using MeetingAssistant.Features.Meetings.Models;
using MeetingAssistant.Shared.Abstractions;

namespace MeetingAssistant.Features.LiveSession.Models
{
    public class PersonalizedMeetingSummary : BaseEntity, IHasOrganizationId
    {
        public Guid MeetingId { get; set; }
        public Meeting Meeting { get; set; } = default!;

        public Guid OrganizationId { get; set; }

        public Guid MeetingParticipantId { get; set; }
        public MeetingParticipant MeetingParticipant { get; set; } = default!;

        public Guid UserId { get; set; }
        public ApplicationUser User { get; set; } = default!;

        public PersonalizedMeetingSummaryStatus Status { get; set; } = PersonalizedMeetingSummaryStatus.Generated;

        public string? SummaryText { get; set; }

        public string? LlmModel { get; set; }

        public int? PromptTokens { get; set; }

        public int? CompletionTokens { get; set; }

        public DateTime? GeneratedAtUtc { get; set; }

        public string? TargetDisplayName { get; set; }

        public string? PromptName { get; set; }

        public string? PromptVersion { get; set; }

        public string? PersonalizationContextJson { get; set; }

        public string? EligibilityReason { get; set; }

        public string? EligibilityContextJson { get; set; }

        public Guid? SourceTranscriptId { get; set; }

        public string? SourceTranscriptHash { get; set; }

        public int? SourceTranscriptRevision { get; set; }

        public DateTime? SourceTranscriptGeneratedAtUtc { get; set; }
    }
}
