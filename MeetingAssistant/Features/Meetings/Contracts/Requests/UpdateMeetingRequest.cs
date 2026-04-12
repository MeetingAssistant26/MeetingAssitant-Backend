namespace MeetingAssistant.Features.Meetings.Contracts.Requests
{
    public sealed record UpdateMeetingRequest(
        string? Title,
        string? Description,
        DateTime? ScheduledStartUtc,
        DateTime? ScheduledEndUtc,
        List<Guid>? TagIds);
}