namespace MeetingAssistant.Features.Meetings.Contracts.Requests
{
    public sealed record UpdateRecurringSeriesRequest(
        string? Title,
        string? Description,
        TimeSpan? ScheduledStartTimeUtc,
        TimeSpan? ScheduledEndTimeUtc,
        RecurrenceConfigDto? Recurrence,
        IReadOnlyList<Guid>? TagIds);
}
