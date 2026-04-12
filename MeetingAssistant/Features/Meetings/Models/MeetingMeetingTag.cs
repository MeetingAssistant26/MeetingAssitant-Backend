using MeetingAssistant.Features.Organizations.Models;

namespace MeetingAssistant.Features.Meetings.Models
{
    public class MeetingMeetingTag
    {
        public Guid MeetingId { get; set; }
        public Meeting Meeting { get; set; } = default!;

        public Guid MeetingTagId { get; set; }
        public MeetingTag MeetingTag { get; set; } = default!;
    }
}