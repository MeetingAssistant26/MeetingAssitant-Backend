namespace MeetingAssistant.Features.Meetings.Contracts.Requests
{
    public sealed record CreateMeetingRequest(
        string Title,
        string? Description,
        DateTime ScheduledStartUtc,
        DateTime ScheduledEndUtc,
        List<Guid>? TagIds);
}