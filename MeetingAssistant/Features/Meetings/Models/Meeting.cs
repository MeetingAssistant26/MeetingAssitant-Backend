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

        public RecurrenceConfig? RecurrenceConfig { get; set; }

        public ICollection<MeetingParticipant> Participants { get; set; } = new List<MeetingParticipant>();

        public ICollection<MeetingMeetingTag> Tags { get; set; } = new List<MeetingMeetingTag>();
    }
}