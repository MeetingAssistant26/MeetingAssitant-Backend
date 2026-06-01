using Mapster;
using MeetingAssistant.Features.Meetings.Contracts.Responses;
using MeetingAssistant.Features.Meetings.Models;
using MeetingAssistant.Infrastructure.Persistence.DbContext;
using Microsoft.EntityFrameworkCore;

namespace MeetingAssistant.Features.Meetings.Services;

public sealed class MeetingConflictService(ApplicationDbContext dbContext) : IMeetingConflictService
{
    private readonly ApplicationDbContext _dbContext = dbContext;

    public async Task<IReadOnlyList<ConflictResponse>> FindConflictsAsync(
        Guid organizationId,
        IReadOnlyCollection<Guid> userIds,
        DateTime startUtc,
        DateTime endUtc,
        Guid? excludeMeetingId = null,
        CancellationToken cancellationToken = default)
    {
        var distinctUserIds = userIds.Where(id => id != Guid.Empty).Distinct().ToList();
        if (distinctUserIds.Count == 0)
            return Array.Empty<ConflictResponse>();

        var query = GetBaseConflictQuery(organizationId, distinctUserIds)
            .Where(m => m.ScheduledStartUtc < endUtc && m.ScheduledEndUtc > startUtc);

        if (excludeMeetingId.HasValue)
        {
            query = query.Where(m => m.Id != excludeMeetingId.Value);
        }

        var overlappingMeetings = await query
            .ProjectToType<MeetingResponse>()
            .ToListAsync(cancellationToken);

        return await BuildConflictResponsesAsync(distinctUserIds, overlappingMeetings, cancellationToken);
    }

    public async Task<IReadOnlyList<OccurrenceConflictResponse>> FindConflictsForOccurrencesAsync(
        Guid organizationId,
        IReadOnlyCollection<Guid> userIds,
        IReadOnlyCollection<ConflictOccurrenceRequest> occurrences,
        Guid? excludeRecurringSeriesId = null,
        CancellationToken cancellationToken = default)
    {
        var distinctUserIds = userIds.Where(id => id != Guid.Empty).Distinct().ToList();
        var validOccurrences = occurrences
            .Where(o => o.ScheduledEndUtc > o.ScheduledStartUtc)
            .OrderBy(o => o.ScheduledStartUtc)
            .ToList();

        if (distinctUserIds.Count == 0 || validOccurrences.Count == 0)
            return Array.Empty<OccurrenceConflictResponse>();

        var minStartUtc = validOccurrences.Min(o => o.ScheduledStartUtc);
        var maxEndUtc = validOccurrences.Max(o => o.ScheduledEndUtc);

        var query = GetBaseConflictQuery(organizationId, distinctUserIds)
            .Where(m => m.ScheduledStartUtc < maxEndUtc && m.ScheduledEndUtc > minStartUtc);

        if (excludeRecurringSeriesId.HasValue)
        {
            query = query.Where(m => m.RecurringSeriesId != excludeRecurringSeriesId.Value);
        }

        var possibleOverlappingMeetings = await query
            .ProjectToType<MeetingResponse>()
            .ToListAsync(cancellationToken);

        if (possibleOverlappingMeetings.Count == 0)
            return Array.Empty<OccurrenceConflictResponse>();

        var occurrenceConflicts = new List<OccurrenceConflictResponse>();

        foreach (var occurrence in validOccurrences)
        {
            var overlappingMeetings = possibleOverlappingMeetings
                .Where(m => m.ScheduledStartUtc < occurrence.ScheduledEndUtc && m.ScheduledEndUtc > occurrence.ScheduledStartUtc)
                .ToList();

            var conflicts = await BuildConflictResponsesAsync(distinctUserIds, overlappingMeetings, cancellationToken);
            if (conflicts.Count > 0)
            {
                occurrenceConflicts.Add(new OccurrenceConflictResponse(
                    occurrence.OccurrenceIndex,
                    occurrence.ScheduledStartUtc,
                    occurrence.ScheduledEndUtc,
                    conflicts.ToList()));
            }
        }

        return occurrenceConflicts;
    }

    private IQueryable<Meeting> GetBaseConflictQuery(Guid organizationId, IReadOnlyCollection<Guid> userIds)
    {
        return _dbContext.Meetings
            .Where(m => m.OrganizationId == organizationId)
            .Where(m => m.Status != MeetingStatus.Cancelled
                        && m.Status != MeetingStatus.Completed
                        && m.Status != MeetingStatus.Failed)
            .Where(m => m.Participants.Any(p => userIds.Contains(p.UserId)));
    }

    private async Task<IReadOnlyList<ConflictResponse>> BuildConflictResponsesAsync(
        IReadOnlyCollection<Guid> distinctUserIds,
        IReadOnlyCollection<MeetingResponse> overlappingMeetings,
        CancellationToken cancellationToken)
    {
        if (overlappingMeetings.Count == 0)
            return Array.Empty<ConflictResponse>();

        var displayNames = await _dbContext.Users
            .IgnoreQueryFilters()
            .Where(u => distinctUserIds.Contains(u.Id))
            .Select(u => new
            {
                u.Id,
                DisplayName = u.DisplayName ?? string.Empty
            })
            .ToDictionaryAsync(u => u.Id, u => u.DisplayName, cancellationToken);

        var conflicts = new List<ConflictResponse>();

        foreach (var userId in distinctUserIds)
        {
            var userConflicts = overlappingMeetings
                .Where(m => m.Participants.Any(p => p.UserId == userId))
                .ToList();

            if (userConflicts.Count > 0)
            {
                conflicts.Add(new ConflictResponse(
                    userId,
                    displayNames.GetValueOrDefault(userId, string.Empty),
                    userConflicts));
            }
        }

        return conflicts;
    }
}
