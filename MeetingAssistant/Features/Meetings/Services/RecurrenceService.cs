using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Mapster;
using MeetingAssistant.Api.Infrastructure.Services;
using MeetingAssistant.Features.Meetings.Contracts.Requests;
using MeetingAssistant.Features.Meetings.Contracts.Responses;
using MeetingAssistant.Features.Meetings.Models;
using MeetingAssistant.Features.Meetings.Models.Events;
using MeetingAssistant.Features.Organizations.Models;
using MeetingAssistant.Infrastructure.Persistence.DbContext;
using MeetingAssistant.Shared.Abstractions;
using MeetingAssistant.Shared.Errors;
using Microsoft.EntityFrameworkCore;

namespace MeetingAssistant.Features.Meetings.Services
{
    public class RecurrenceService(
        ApplicationDbContext dbContext,
        ITenantProvider tenantProvider) : IRecurrenceService
    {
        private readonly ApplicationDbContext _dbContext = dbContext;
        private readonly ITenantProvider _tenantProvider = tenantProvider;

        private Guid GetOrganizationId()
        {
            var orgId = _tenantProvider.CurrentOrganizationId ?? Guid.Empty;
            if (orgId == Guid.Empty)
                throw new UnauthorizedAccessException("Organization context is missing.");
            return orgId;
        }

        public async Task<Result<RecurringMeetingCreationResponse>> GenerateMeetingInstancesAsync(
            Guid userId,
            CreateRecurringMeetingRequest request,
            CancellationToken cancellationToken = default)
        {
            var organizationId = GetOrganizationId();

            var membership = await _dbContext.UserOrgMemberships
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(m => m.UserId == userId && m.OrganizationId == organizationId && m.IsEnabled, cancellationToken);

            if (membership == null)
                return Result.Failure<RecurringMeetingCreationResponse>(MeetingErrors.NotOrgMember);

            if (membership.OrgRole == OrganizationRole.Guest)
                return Result.Failure<RecurringMeetingCreationResponse>(MeetingErrors.GuestNotAllowed);

            var tags = new List<MeetingTag>();
            if (request.TagIds != null && request.TagIds.Any())
            {
                tags = await _dbContext.MeetingTags
                    .Where(t => request.TagIds.Contains(t.Id) && t.OrganizationId == organizationId && t.IsActive)
                    .ToListAsync(cancellationToken);

                if (tags.Count != request.TagIds.Count)
                {
                    return Result.Failure<RecurringMeetingCreationResponse>(MeetingErrors.TagNotFound);
                }
            }

            var limitDate = DateTime.UtcNow.Date.AddDays(7 * 12); // Default 12 weeks
            if (request.Recurrence.EndsAtUtc.HasValue)
            {
                var maxLimit = DateTime.UtcNow.Date.AddDays(7 * 52); // Max 52 weeks
                limitDate = request.Recurrence.EndsAtUtc.Value > maxLimit ? maxLimit : request.Recurrence.EndsAtUtc.Value;
            }

            var startDates = CalculateOccurrenceDates(
                DateTime.UtcNow.Date,
                request.ScheduledStartTimeUtc,
                request.Recurrence,
                limitDate);

            if (!startDates.Any())
                return Result.Failure<RecurringMeetingCreationResponse>(MeetingErrors.InvalidRecurrence);

            var series = new RecurringMeetingSeries
            {
                Id = Guid.NewGuid(),
                OrganizationId = organizationId,
                Title = request.Title,
                Description = request.Description,
                ScheduledStartTimeUtc = request.ScheduledStartTimeUtc,
                ScheduledEndTimeUtc = request.ScheduledEndTimeUtc,
                Frequency = request.Recurrence.Frequency,
                Interval = request.Recurrence.Interval,
                DaysOfWeek = DaysOfWeekToString(request.Recurrence.DaysOfWeek),
                EndsAtUtc = request.Recurrence.EndsAtUtc,
                Status = RecurringMeetingSeriesStatus.Active,
                CreatedByUserId = userId
            };

            var meetings = new List<Meeting>();
            var occurrenceIndex = 0;

            foreach (var date in startDates)
            {
                var startUtc = date.Add(request.ScheduledStartTimeUtc);
                var endUtc = date.Add(request.ScheduledEndTimeUtc);

                var meeting = new Meeting
                {
                    Id = Guid.NewGuid(),
                    OrganizationId = organizationId,
                    Title = request.Title,
                    Description = request.Description,
                    ScheduledStartUtc = startUtc,
                    ScheduledEndUtc = endUtc,
                    Status = MeetingStatus.Scheduled,
                    RecurringSeriesId = series.Id,
                    RecurringOccurrenceIndex = occurrenceIndex++,
                    RecurrenceConfig = new RecurrenceConfig
                    {
                        Frequency = request.Recurrence.Frequency,
                        Interval = request.Recurrence.Interval,
                        DaysOfWeek = DaysOfWeekToString(request.Recurrence.DaysOfWeek),
                        EndsAtUtc = request.Recurrence.EndsAtUtc
                    }
                };

                meeting.Participants.Add(new MeetingParticipant
                {
                    MeetingId = meeting.Id,
                    OrganizationId = organizationId,
                    UserId = userId,
                    MeetingRole = MeetingRole.Host
                });

                foreach (var tag in tags)
                {
                    meeting.Tags.Add(new MeetingMeetingTag
                    {
                        MeetingId = meeting.Id,
                        MeetingTagId = tag.Id
                    });
                }

                meeting.RaiseDomainEvent(new MeetingCreatedEvent(organizationId, meeting.Id));
                meetings.Add(meeting);
            }

            _dbContext.RecurringMeetingSeries.Add(series);
            _dbContext.Meetings.AddRange(meetings);
            await _dbContext.SaveChangesAsync(cancellationToken);

            var responses = meetings.Adapt<IReadOnlyList<MeetingResponse>>();
            return Result.Success(new RecurringMeetingCreationResponse(responses.Count, responses, series.Id));
        }

        public async Task<Result<RecurringSeriesListResponse>> ListRecurringSeriesAsync(
            int page,
            int pageSize,
            CancellationToken cancellationToken = default)
        {
            page = Math.Max(1, page);
            pageSize = Math.Clamp(pageSize, 1, 100);

            var query = _dbContext.RecurringMeetingSeries
                .AsNoTracking()
                .OrderByDescending(s => s.CreatedAtUtc)
                .ThenBy(s => s.Title);

            var totalCount = await query.CountAsync(cancellationToken);

            var series = await query
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync(cancellationToken);

            var ids = series.Select(s => s.Id).ToList();
            var occurrenceLookup = await LoadOccurrenceLookupAsync(ids, cancellationToken);

            var items = series
                .Select(s => MapSeriesResponse(s, occurrenceLookup.GetValueOrDefault(s.Id, new List<Meeting>())))
                .ToList();

            return Result.Success(new RecurringSeriesListResponse(items, totalCount, page, pageSize));
        }

        public async Task<Result<RecurringSeriesResponse>> GetRecurringSeriesAsync(
            Guid seriesId,
            CancellationToken cancellationToken = default)
        {
            var series = await _dbContext.RecurringMeetingSeries
                .AsNoTracking()
                .FirstOrDefaultAsync(s => s.Id == seriesId, cancellationToken);

            if (series == null)
                return Result.Failure<RecurringSeriesResponse>(MeetingErrors.NotFound);

            var occurrences = await LoadOccurrencesAsync(seriesId, cancellationToken);
            return Result.Success(MapSeriesResponse(series, occurrences));
        }

        public async Task<Result<RecurringSeriesResponse>> UpdateRecurringSeriesAsync(
            Guid seriesId,
            Guid userId,
            UpdateRecurringSeriesRequest request,
            CancellationToken cancellationToken = default)
        {
            var organizationId = GetOrganizationId();

            var series = await _dbContext.RecurringMeetingSeries
                .Include(s => s.Meetings)
                    .ThenInclude(m => m.Tags)
                .Include(s => s.Meetings)
                    .ThenInclude(m => m.Participants)
                .FirstOrDefaultAsync(s => s.Id == seriesId, cancellationToken);

            if (series == null)
                return Result.Failure<RecurringSeriesResponse>(MeetingErrors.NotFound);

            var auth = await EnsureSeriesManagementAllowedAsync(series, userId, cancellationToken);
            if (auth.IsFailure)
                return Result.Failure<RecurringSeriesResponse>(auth.Error);

            if (series.Status == RecurringMeetingSeriesStatus.Cancelled)
                return Result.Failure<RecurringSeriesResponse>(MeetingErrors.InvalidLifecycleTransition);

            var nextTitle = request.Title ?? series.Title;
            var nextDescription = request.Description ?? series.Description;
            var nextStartTime = request.ScheduledStartTimeUtc ?? series.ScheduledStartTimeUtc;
            var nextEndTime = request.ScheduledEndTimeUtc ?? series.ScheduledEndTimeUtc;
            var nextRecurrence = request.Recurrence ?? ToRecurrenceConfigDto(series);

            if (nextEndTime <= nextStartTime)
                return Result.Failure<RecurringSeriesResponse>(MeetingErrors.InvalidRecurrence);

            var effectiveTagIds = request.TagIds ?? series.Meetings
                .SelectMany(m => m.Tags.Select(t => t.MeetingTagId))
                .Distinct()
                .ToList();

            var tags = await LoadAndValidateTagsAsync(effectiveTagIds, organizationId, cancellationToken);
            if (tags.IsFailure)
                return Result.Failure<RecurringSeriesResponse>(tags.Error);

            var now = DateTime.UtcNow;
            var futureScheduled = series.Meetings
                .Where(m => m.ScheduledStartUtc >= now && m.Status == MeetingStatus.Scheduled)
                .OrderBy(m => m.ScheduledStartUtc)
                .ToList();

            var patternChanged = request.ScheduledStartTimeUtc.HasValue
                || request.ScheduledEndTimeUtc.HasValue
                || request.Recurrence != null;

            series.Title = nextTitle;
            series.Description = nextDescription;
            series.ScheduledStartTimeUtc = nextStartTime;
            series.ScheduledEndTimeUtc = nextEndTime;
            series.Frequency = nextRecurrence.Frequency;
            series.Interval = nextRecurrence.Interval;
            series.DaysOfWeek = DaysOfWeekToString(nextRecurrence.DaysOfWeek);
            series.EndsAtUtc = nextRecurrence.EndsAtUtc;

            if (patternChanged)
            {
                foreach (var occurrence in futureScheduled)
                {
                    occurrence.Status = MeetingStatus.Cancelled;
                    occurrence.RaiseDomainEvent(new MeetingCancelledEvent(organizationId, occurrence.Id));
                }

                var generated = GenerateOccurrenceMeetings(
                    series,
                    GetParticipantTemplate(series, futureScheduled),
                    tags.Value,
                    CalculateOccurrenceDates(DateTime.UtcNow.Date, nextStartTime, nextRecurrence, ResolveLimitDate(nextRecurrence)));

                if (!generated.Any())
                    return Result.Failure<RecurringSeriesResponse>(MeetingErrors.InvalidRecurrence);

                _dbContext.Meetings.AddRange(generated);
            }
            else
            {
                foreach (var occurrence in futureScheduled)
                {
                    occurrence.Title = nextTitle;
                    occurrence.Description = nextDescription;

                    if (request.TagIds != null)
                    {
                        _dbContext.MeetingMeetingTags.RemoveRange(occurrence.Tags);
                        occurrence.Tags.Clear();
                        AddTags(occurrence, tags.Value);
                    }

                    occurrence.RaiseDomainEvent(new MeetingUpdatedEvent(organizationId, occurrence.Id));
                }
            }

            await _dbContext.SaveChangesAsync(cancellationToken);

            var occurrences = await LoadOccurrencesAsync(series.Id, cancellationToken);
            return Result.Success(MapSeriesResponse(series, occurrences));
        }

        public async Task<Result<RecurringSeriesResponse>> DeleteRecurringSeriesAsync(
            Guid seriesId,
            Guid userId,
            CancellationToken cancellationToken = default)
        {
            var series = await _dbContext.RecurringMeetingSeries
                .Include(s => s.Meetings)
                .FirstOrDefaultAsync(s => s.Id == seriesId, cancellationToken);

            if (series == null)
                return Result.Failure<RecurringSeriesResponse>(MeetingErrors.NotFound);

            var auth = await EnsureSeriesManagementAllowedAsync(series, userId, cancellationToken);
            if (auth.IsFailure)
                return Result.Failure<RecurringSeriesResponse>(auth.Error);

            if (series.Status == RecurringMeetingSeriesStatus.Active)
            {
                var now = DateTime.UtcNow;
                series.Status = RecurringMeetingSeriesStatus.Cancelled;
                series.CancelledAtUtc = now;

                foreach (var occurrence in series.Meetings.Where(m => m.ScheduledStartUtc >= now && m.Status == MeetingStatus.Scheduled))
                {
                    occurrence.Status = MeetingStatus.Cancelled;
                    occurrence.RaiseDomainEvent(new MeetingCancelledEvent(series.OrganizationId, occurrence.Id));
                }

                await _dbContext.SaveChangesAsync(cancellationToken);
            }

            var occurrences = await LoadOccurrencesAsync(series.Id, cancellationToken);
            return Result.Success(MapSeriesResponse(series, occurrences));
        }

        private List<Meeting> GenerateOccurrenceMeetings(
            RecurringMeetingSeries series,
            IReadOnlyList<(Guid UserId, MeetingRole Role)> participants,
            IReadOnlyList<MeetingTag> tags,
            IReadOnlyList<DateTime> dates)
        {
            var meetings = new List<Meeting>();
            var nextOccurrenceIndex = series.Meetings
                .Where(m => m.RecurringOccurrenceIndex.HasValue)
                .Select(m => m.RecurringOccurrenceIndex!.Value)
                .DefaultIfEmpty(-1)
                .Max() + 1;

            var recurrence = ToRecurrenceConfigDto(series);

            foreach (var date in dates)
            {
                var meeting = new Meeting
                {
                    Id = Guid.NewGuid(),
                    OrganizationId = series.OrganizationId,
                    Title = series.Title,
                    Description = series.Description,
                    ScheduledStartUtc = date.Add(series.ScheduledStartTimeUtc),
                    ScheduledEndUtc = date.Add(series.ScheduledEndTimeUtc),
                    Status = MeetingStatus.Scheduled,
                    RecurringSeriesId = series.Id,
                    RecurringOccurrenceIndex = nextOccurrenceIndex++,
                    RecurrenceConfig = new RecurrenceConfig
                    {
                        Frequency = recurrence.Frequency,
                        Interval = recurrence.Interval,
                        DaysOfWeek = DaysOfWeekToString(recurrence.DaysOfWeek),
                        EndsAtUtc = recurrence.EndsAtUtc
                    }
                };

                foreach (var participant in participants)
                {
                    meeting.Participants.Add(new MeetingParticipant
                    {
                        MeetingId = meeting.Id,
                        OrganizationId = series.OrganizationId,
                        UserId = participant.UserId,
                        MeetingRole = participant.Role
                    });
                }

                AddTags(meeting, tags);
                meeting.RaiseDomainEvent(new MeetingCreatedEvent(series.OrganizationId, meeting.Id));
                meetings.Add(meeting);
            }

            return meetings;
        }

        private static IReadOnlyList<(Guid UserId, MeetingRole Role)> GetParticipantTemplate(
            RecurringMeetingSeries series,
            IReadOnlyList<Meeting> futureScheduled)
        {
            var participants = futureScheduled
                .SelectMany(m => m.Participants)
                .GroupBy(p => p.UserId)
                .Select(g =>
                {
                    var role = g.Select(p => p.MeetingRole).OrderBy(r => (int)r).First();
                    return (UserId: g.Key, Role: role);
                })
                .ToList();

            if (participants.Any(p => p.Role == MeetingRole.Host))
                return participants;

            participants.Add((series.CreatedByUserId, MeetingRole.Host));
            return participants;
        }

        private async Task<Result<IReadOnlyList<MeetingTag>>> LoadAndValidateTagsAsync(
            IReadOnlyList<Guid>? tagIds,
            Guid organizationId,
            CancellationToken cancellationToken)
        {
            if (tagIds == null)
                return Result.Success<IReadOnlyList<MeetingTag>>(Array.Empty<MeetingTag>());

            if (tagIds.Count == 0)
                return Result.Success<IReadOnlyList<MeetingTag>>(Array.Empty<MeetingTag>());

            var tags = await _dbContext.MeetingTags
                .IgnoreQueryFilters()
                .Where(t => tagIds.Contains(t.Id) && t.OrganizationId == organizationId && t.IsActive)
                .ToListAsync(cancellationToken);

            return tags.Count == tagIds.Count
                ? Result.Success<IReadOnlyList<MeetingTag>>(tags)
                : Result.Failure<IReadOnlyList<MeetingTag>>(MeetingErrors.TagNotFound);
        }

        private static void AddTags(Meeting meeting, IReadOnlyList<MeetingTag> tags)
        {
            foreach (var tag in tags)
            {
                meeting.Tags.Add(new MeetingMeetingTag
                {
                    MeetingId = meeting.Id,
                    MeetingTagId = tag.Id
                });
            }
        }

        private async Task<Result> EnsureSeriesManagementAllowedAsync(
            RecurringMeetingSeries series,
            Guid userId,
            CancellationToken cancellationToken)
        {
            var membershipRole = await _dbContext.UserOrgMemberships
                .IgnoreQueryFilters()
                .Where(m => m.UserId == userId && m.OrganizationId == series.OrganizationId && m.IsEnabled)
                .Select(m => (OrganizationRole?)m.OrgRole)
                .FirstOrDefaultAsync(cancellationToken);

            if (membershipRole is null)
                return Result.Failure(MeetingErrors.NotOrgMember);

            if (membershipRole == OrganizationRole.Guest)
                return Result.Failure(MeetingErrors.GuestNotAllowed);

            if (membershipRole == OrganizationRole.Admin || series.CreatedByUserId == userId)
                return Result.Success();

            var isHostOrCoHost = await _dbContext.MeetingParticipants
                .IgnoreQueryFilters()
                .AnyAsync(p => p.OrganizationId == series.OrganizationId
                            && p.UserId == userId
                            && p.Meeting.RecurringSeriesId == series.Id
                            && (p.MeetingRole == MeetingRole.Host || p.MeetingRole == MeetingRole.CoHost), cancellationToken);

            return isHostOrCoHost
                ? Result.Success()
                : Result.Failure(MeetingErrors.NotHost);
        }

        private async Task<Dictionary<Guid, List<Meeting>>> LoadOccurrenceLookupAsync(
            IReadOnlyList<Guid> seriesIds,
            CancellationToken cancellationToken)
        {
            if (seriesIds.Count == 0)
                return new Dictionary<Guid, List<Meeting>>();

            var occurrences = await _dbContext.Meetings
                .AsNoTracking()
                .Include(m => m.Participants).ThenInclude(p => p.User)
                .Include(m => m.Tags)
                .Where(m => m.RecurringSeriesId.HasValue && seriesIds.Contains(m.RecurringSeriesId.Value))
                .OrderBy(m => m.ScheduledStartUtc)
                .ToListAsync(cancellationToken);

            return occurrences
                .GroupBy(m => m.RecurringSeriesId!.Value)
                .ToDictionary(g => g.Key, g => g.ToList());
        }

        private async Task<List<Meeting>> LoadOccurrencesAsync(Guid seriesId, CancellationToken cancellationToken)
        {
            return await _dbContext.Meetings
                .AsNoTracking()
                .Include(m => m.Participants).ThenInclude(p => p.User)
                .Include(m => m.Tags)
                .Where(m => m.RecurringSeriesId == seriesId)
                .OrderBy(m => m.ScheduledStartUtc)
                .ToListAsync(cancellationToken);
        }

        private static RecurringSeriesResponse MapSeriesResponse(RecurringMeetingSeries series, IReadOnlyList<Meeting> occurrences)
        {
            var now = DateTime.UtcNow;
            return new RecurringSeriesResponse(
                series.Id,
                series.OrganizationId,
                series.Title,
                series.Description,
                series.ScheduledStartTimeUtc,
                series.ScheduledEndTimeUtc,
                ToRecurrenceConfigDto(series),
                series.Status,
                series.CreatedByUserId,
                series.CancelledAtUtc,
                series.CreatedAtUtc,
                series.UpdatedAtUtc,
                occurrences.Count,
                occurrences.Count(m => m.ScheduledStartUtc >= now && m.Status == MeetingStatus.Scheduled),
                occurrences.Adapt<IReadOnlyList<MeetingResponse>>());
        }

        private static RecurrenceConfigDto ToRecurrenceConfigDto(RecurringMeetingSeries series)
        {
            return new RecurrenceConfigDto(
                series.Frequency,
                series.Interval,
                ParseDaysOfWeek(series.DaysOfWeek),
                series.EndsAtUtc);
        }

        private static string? DaysOfWeekToString(IReadOnlyList<DayOfWeek>? daysOfWeek)
        {
            return daysOfWeek is { Count: > 0 }
                ? string.Join(",", daysOfWeek)
                : null;
        }

        private static IReadOnlyList<DayOfWeek>? ParseDaysOfWeek(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return null;

            return value
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(v => Enum.Parse<DayOfWeek>(v, ignoreCase: true))
                .ToList();
        }

        private static DateTime ResolveLimitDate(RecurrenceConfigDto recurrence)
        {
            var limitDate = DateTime.UtcNow.Date.AddDays(7 * 12);
            if (!recurrence.EndsAtUtc.HasValue)
                return limitDate;

            var maxLimit = DateTime.UtcNow.Date.AddDays(7 * 52);
            return recurrence.EndsAtUtc.Value > maxLimit ? maxLimit : recurrence.EndsAtUtc.Value;
        }

        private static List<DateTime> CalculateOccurrenceDates(
            DateTime today,
            TimeSpan startTime,
            RecurrenceConfigDto config,
            DateTime limitDate)
        {
            var dates = new List<DateTime>();
            var current = today;

            if (current.Add(startTime) < DateTime.UtcNow)
            {
                current = current.AddDays(1);
            }

            // Snap to the first valid start date depending on frequency
            if (config.Frequency == RecurrenceFrequency.Monthly)
            {
                // Align with the original requested creation day, since today.Day might not be the true intended day
                // Actually start from today for now unless specified in spec.
                // Let's iterate month by month proper rather than checking Day manually
                int monthIteration = 0;
                while (dates.Count < 365)
                {
                    var targetMonth = current.AddMonths(monthIteration * config.Interval);
                    if (targetMonth > limitDate) break;
                    
                    dates.Add(targetMonth);
                    monthIteration++;
                }
                return dates;
            }

            if (config.Frequency == RecurrenceFrequency.Weekly)
            {
                // Find all matching days in bounded window week-by-week
                // Align current to start of its week (Monday)
                int diff = (7 + (current.DayOfWeek - DayOfWeek.Monday)) % 7;
                var weekStart = current.AddDays(-1 * diff);
                int weekIteration = 0;

                while (dates.Count < 365)
                {
                    var targetWeekStart = weekStart.AddDays(weekIteration * config.Interval * 7);
                    if (targetWeekStart > limitDate) break;
                    
                    // Iterate through the week to find matching days
                    for (int i = 0; i < 7; i++)
                    {
                        var candidate = targetWeekStart.AddDays(i);
                        if (candidate < current) continue; // Skip days before "today" in the first week
                        if (candidate > limitDate) break;

                        if (config.DaysOfWeek != null && config.DaysOfWeek.Contains(candidate.DayOfWeek))
                        {
                            dates.Add(candidate);
                        }
                    }
                    weekIteration++;
                }
                return dates;
            }

            if (config.Frequency == RecurrenceFrequency.Daily)
            {
                int dayIteration = 0;
                while (dates.Count < 365)
                {
                    var targetDay = current.AddDays(dayIteration * config.Interval);
                    if (targetDay > limitDate) break;
                    
                    dates.Add(targetDay);
                    dayIteration++;
                }
                return dates;
            }

            return dates;
        }
    }
}
