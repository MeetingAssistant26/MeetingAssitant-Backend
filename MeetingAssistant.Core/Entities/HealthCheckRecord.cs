namespace MeetingAssistant.Core.Entities
{
    public class HealthCheckRecord
    {
        public int Id { get; set; }

        public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

        public string Source { get; set; } = "api-init";
    }
}
