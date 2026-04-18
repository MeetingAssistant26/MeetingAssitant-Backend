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
            DateTime? targetWeek,
            CancellationToken cancellationToken = default)
        {
            var baseDate = targetWeek?.Date ?? DateTime.UtcNow.Date;

            // Snap to Monday
            var diff = (7 + (baseDate.DayOfWeek - DayOfWeek.Monday)) % 7;
            var startOfWeek = baseDate.AddDays(-1 * diff);
            var endOfWeek = startOfWeek.AddDays(7);

            var meetings = await _dbContext.Meetings
                .Where(m => m.ScheduledStartUtc >= startOfWeek && m.ScheduledStartUtc < endOfWeek)
                .OrderBy(m => m.ScheduledStartUtc)
                .ProjectToType<MeetingResponse>()
                .ToListAsync(cancellationToken);

            return Result.Success(new CalendarDataResponse(startOfWeek, endOfWeek, meetings));
        }
    }
}