using MeetingAssistant.Shared.Abstractions;
using MeetingAssistant.Api.Shared;

namespace MeetingAssistant.Features.Meetings.Models
{
    public class Meeting : BaseEntity, IHasOrganizationId
    {
        public Guid OrganizationId { get; set; }

        public string Title { get; set; } = string.Empty;

        public string? Description { get; set; }

        public DateTime ScheduledStartUtc { get; set; }

        public DateTime ScheduledEndUtc { get; set; }

        public MeetingStatus Status { get; set; } = MeetingStatus.Scheduled;

        /// <summary>
        /// Canonical UTC timestamp for room/session activation. Set once by the first
        /// successful REST start or verified LiveKit room_started webhook and never moved
        /// by duplicate/out-of-order lifecycle events.
        /// </summary>
        public DateTime? RoomActivatedAtUtc { get; set; }

        public RecurrenceConfig? RecurrenceConfig { get; set; }

        public ICollection<MeetingParticipant> Participants { get; set; } = new List<MeetingParticipant>();

        public ICollection<MeetingMeetingTag> Tags { get; set; } = new List<MeetingMeetingTag>();
    }
}
