using MeetingAssistant.Features.Meetings.Models;

namespace MeetingAssistant.Features.AgentApi.Models.Responses
{
    public sealed record AgentMeetingResponse(
        Guid Id,
        string Title,
        DateTime ScheduledStartUtc,
        DateTime ScheduledEndUtc,
        string Status,
        RecurrenceConfig? RecurrenceConfig,
        List<Guid> TagIds);
}
