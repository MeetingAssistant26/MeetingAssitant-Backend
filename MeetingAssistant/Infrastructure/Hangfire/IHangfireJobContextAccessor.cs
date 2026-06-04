namespace MeetingAssistant.Api.Infrastructure.Hangfire;

public interface IHangfireJobContextAccessor
{
    string? CurrentJobId { get; set; }
}
