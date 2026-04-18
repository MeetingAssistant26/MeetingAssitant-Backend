using MeetingAssistant.Features.Meetings.Contracts.Responses;
using MeetingAssistant.Shared.Abstractions;

namespace MeetingAssistant.Features.Meetings.Services
{
    public interface ICalendarService
    {
        Task<Result<CalendarDataResponse>> GetCalendarDataAsync(
            DateTime? targetWeek,
            CancellationToken cancellationToken = default);
    }
}