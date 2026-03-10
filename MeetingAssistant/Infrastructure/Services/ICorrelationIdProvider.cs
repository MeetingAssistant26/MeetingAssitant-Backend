namespace MeetingAssistant.Api.Infrastructure.Services
{
    public interface ICorrelationIdProvider
    {
        string? CorrelationId { get; set; }
    }
}
