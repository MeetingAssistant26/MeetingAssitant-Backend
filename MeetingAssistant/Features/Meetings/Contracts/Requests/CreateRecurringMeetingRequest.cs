using MeetingAssistant.Features.Meetings.Models;

namespace MeetingAssistant.Features.Meetings.Contracts.Requests
{
    public sealed record RecurrenceConfigDto(
        RecurrenceFrequency Frequency,
        int Interval,
        IReadOnlyList<DayOfWeek>? DaysOfWeek,
        DateTime? EndsAtUtc);

    public sealed record CreateRecurringMeetingRequest(
        string Title,
        string? Description,
        TimeSpan ScheduledStartTimeUtc,
        TimeSpan ScheduledEndTimeUtc,
        RecurrenceConfigDto Recurrence,
        IReadOnlyList<Guid>? TagIds)
    {
        public List<CreateMeetingParticipantRequest>? Participants { get; init; }
    }

    public sealed record RecurringMeetingConflictCheckRequest(
        TimeSpan ScheduledStartTimeUtc,
        TimeSpan ScheduledEndTimeUtc,
        RecurrenceConfigDto Recurrence,
        List<Guid>? ParticipantUserIds,
        Guid? ExcludeRecurringSeriesId);
}
