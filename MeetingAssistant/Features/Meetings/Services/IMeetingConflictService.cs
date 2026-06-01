using MeetingAssistant.Features.Meetings.Contracts.Responses;

namespace MeetingAssistant.Features.Meetings.Services;

public interface IMeetingConflictService
{
    Task<IReadOnlyList<ConflictResponse>> FindConflictsAsync(
        Guid organizationId,
        IReadOnlyCollection<Guid> userIds,
        DateTime startUtc,
        DateTime endUtc,
        Guid? excludeMeetingId = null,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<OccurrenceConflictResponse>> FindConflictsForOccurrencesAsync(
        Guid organizationId,
        IReadOnlyCollection<Guid> userIds,
        IReadOnlyCollection<ConflictOccurrenceRequest> occurrences,
        Guid? excludeRecurringSeriesId = null,
        CancellationToken cancellationToken = default);
}
