using MeetingAssistant.Api.Shared;
using MeetingAssistant.Shared.Abstractions;

namespace MeetingAssistant.Features.Meetings.Models
{
    /// <summary>
    /// First-class user-facing recurring series identity. Individual Meeting rows remain
    /// the concrete occurrences, while series updates/deletes apply only to future
    /// linked scheduled occurrences. One-off occurrence override modeling is intentionally
    /// unsupported in this pass.
    /// </summary>
    public class RecurringMeetingSeries : BaseEntity, IHasOrganizationId
    {
        public Guid OrganizationId { get; set; }

        public string Title { get; set; } = string.Empty;

        public string? Description { get; set; }

        public TimeSpan ScheduledStartTimeUtc { get; set; }

        public TimeSpan ScheduledEndTimeUtc { get; set; }

        public RecurrenceFrequency Frequency { get; set; }

        public int Interval { get; set; }

        public string? DaysOfWeek { get; set; }

        public DateTime? EndsAtUtc { get; set; }

        public RecurringMeetingSeriesStatus Status { get; set; } = RecurringMeetingSeriesStatus.Active;

        public Guid CreatedByUserId { get; set; }

        public DateTime? CancelledAtUtc { get; set; }

        public ICollection<Meeting> Meetings { get; set; } = new List<Meeting>();
    }
}
