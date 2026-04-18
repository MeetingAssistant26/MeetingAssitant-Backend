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

            var meetings = new List<Meeting>();

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
                    RecurrenceConfig = new RecurrenceConfig
                    {
                        Frequency = request.Recurrence.Frequency,
                        Interval = request.Recurrence.Interval,
                        DaysOfWeek = request.Recurrence.DaysOfWeek != null ? string.Join(",", request.Recurrence.DaysOfWeek) : null,
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

            _dbContext.Meetings.AddRange(meetings);
            await _dbContext.SaveChangesAsync(cancellationToken);

            var responses = meetings.Adapt<IReadOnlyList<MeetingResponse>>();
            return Result.Success(new RecurringMeetingCreationResponse(responses.Count, responses));
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
