using MeetingAssistant.Api.Shared;
using MeetingAssistant.Features.Meetings.Models;
using MeetingAssistant.Shared.Abstractions;

namespace MeetingAssistant.Features.LiveSession.Models
{
    public class AiAssistantTraceEvent : BaseEntity, IHasOrganizationId
    {
        public Guid OrganizationId { get; set; }

        public Guid MeetingId { get; set; }
        public Meeting Meeting { get; set; } = default!;

        public string SessionId { get; set; } = string.Empty;

        public string TurnId { get; set; } = string.Empty;

        public int Sequence { get; set; }

        public string EventType { get; set; } = string.Empty;

        public DateTime OccurredAtUtc { get; set; }

        public string? ParticipantIdentity { get; set; }

        public string? State { get; set; }

        public string? StepType { get; set; }

        public string? StepProvider { get; set; }

        public string? StepEndpoint { get; set; }

        public string? StepModel { get; set; }

        public string? StepVoice { get; set; }

        public int? DurationMs { get; set; }

        public int? PromptTokens { get; set; }

        public int? CompletionTokens { get; set; }

        public int? TotalTokens { get; set; }

        public int? CharactersCount { get; set; }

        public int? AudioDurationMs { get; set; }

        public string? RequestPayloadJson { get; set; }

        public string? ResponsePayloadJson { get; set; }

        public string? Text { get; set; }

        public string? ErrorMessage { get; set; }

        public string? ErrorType { get; set; }
    }
}
