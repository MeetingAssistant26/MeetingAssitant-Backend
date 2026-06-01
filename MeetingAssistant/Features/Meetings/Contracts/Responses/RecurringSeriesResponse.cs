using MeetingAssistant.Features.Meetings.Contracts.Requests;
using MeetingAssistant.Features.Meetings.Models;

namespace MeetingAssistant.Features.Meetings.Contracts.Responses
{
    public sealed record RecurringSeriesResponse(
        Guid Id,
        Guid OrganizationId,
        string Title,
        string? Description,
        TimeSpan ScheduledStartTimeUtc,
        TimeSpan ScheduledEndTimeUtc,
        RecurrenceConfigDto Recurrence,
        RecurringMeetingSeriesStatus Status,
        Guid CreatedByUserId,
        DateTime? CancelledAtUtc,
        DateTime CreatedAtUtc,
        DateTime UpdatedAtUtc,
        int OccurrenceCount,
        int FutureScheduledOccurrenceCount,
        IReadOnlyList<MeetingResponse> Occurrences,
        string UpdateSemantics = "Future linked scheduled occurrences only; whole-history rewrites and one-off occurrence overrides are unsupported.");

    public sealed record RecurringSeriesListResponse(
        IReadOnlyList<RecurringSeriesResponse> Items,
        int TotalCount,
        int Page,
        int PageSize);
}
