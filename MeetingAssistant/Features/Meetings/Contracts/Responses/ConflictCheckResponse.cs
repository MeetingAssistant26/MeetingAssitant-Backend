namespace MeetingAssistant.Features.Meetings.Contracts.Responses;

public record ConflictCheckResponse(List<ConflictResponse> Conflicts);

public sealed record ConflictOccurrenceRequest(
    int OccurrenceIndex,
    DateTime ScheduledStartUtc,
    DateTime ScheduledEndUtc);

public sealed record OccurrenceConflictResponse(
    int OccurrenceIndex,
    DateTime ScheduledStartUtc,
    DateTime ScheduledEndUtc,
    List<ConflictResponse> Conflicts);

public sealed record RecurringConflictCheckResponse(
    int TotalOccurrencesChecked,
    int TotalConflictOccurrences,
    List<OccurrenceConflictResponse> Conflicts);
