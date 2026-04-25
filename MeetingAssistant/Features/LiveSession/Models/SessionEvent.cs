using MeetingAssistant.Api.Shared;
using MeetingAssistant.Features.Meetings.Models;
using MeetingAssistant.Shared.Abstractions;

namespace MeetingAssistant.Features.LiveSession.Models
{
    public class SessionEvent : BaseEntity, IHasOrganizationId
    {
        public Guid MeetingId { get; set; }
        public Meeting Meeting { get; set; } = default!;

        public Guid OrganizationId { get; set; }

        public string ExternalEventId { get; set; } = string.Empty;

        public SessionEventType EventType { get; set; }

        public Guid? ParticipantUserId { get; set; }

        public string PayloadJson { get; set; } = "{}";

        public DateTime OccurredAtUtc { get; set; }

        public DateTime ProcessedAtUtc { get; set; }
    }
}
