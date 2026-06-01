using Mapster;
using MeetingAssistant.Features.Meetings.Contracts.Responses;
using MeetingAssistant.Infrastructure.Persistence.DbContext;
using MeetingAssistant.Shared.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace MeetingAssistant.Features.Meetings.Services
{
    public class CalendarService(ApplicationDbContext dbContext) : ICalendarService
    {
        private readonly ApplicationDbContext _dbContext = dbContext;

        public async Task<Result<CalendarDataResponse>> GetCalendarDataAsync(
            Guid organizationId,
            DateTime? targetWeek,
            CancellationToken cancellationToken = default)
        {
            var baseDate = targetWeek.HasValue
                ? ToUtcCalendarDate(targetWeek.Value)
                : DateTime.UtcNow.Date;

            // Calendar weeks are computed in UTC so browser/mobile callers do not
            // accidentally shift tenant calendar windows through server-local time.
            var diff = (7 + (baseDate.DayOfWeek - DayOfWeek.Monday)) % 7;
            var startOfWeek = DateTime.SpecifyKind(baseDate.AddDays(-1 * diff), DateTimeKind.Utc);
            var endOfWeek = startOfWeek.AddDays(7);

            var meetings = await _dbContext.Meetings
                .Where(m => m.OrganizationId == organizationId)
                .Where(m => m.ScheduledStartUtc >= startOfWeek && m.ScheduledStartUtc < endOfWeek)
                .OrderBy(m => m.ScheduledStartUtc)
                .ProjectToType<MeetingResponse>()
                .ToListAsync(cancellationToken);

            return Result.Success(new CalendarDataResponse(startOfWeek, endOfWeek, meetings));
        }

        private static DateTime ToUtcCalendarDate(DateTime week)
        {
            return week.Kind switch
            {
                DateTimeKind.Utc => week.Date,
                DateTimeKind.Local => week.ToUniversalTime().Date,
                _ => DateTime.SpecifyKind(week.Date, DateTimeKind.Utc)
            };
        }
    }
}
