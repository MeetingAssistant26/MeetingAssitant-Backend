namespace MeetingAssistant.Features.Meetings.Models
{
    public class RecurrenceConfig
    {
        public RecurrenceFrequency Frequency { get; set; }
        public int Interval { get; set; }
        public string? DaysOfWeek { get; set; } // e.g., "Monday,Wednesday,Friday"
        public DateTime? EndsAtUtc { get; set; }
    }
}