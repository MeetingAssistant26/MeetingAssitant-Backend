using MeetingAssistant.Features.Meetings.Contracts.Requests;
using MeetingAssistant.Features.Meetings.Contracts.Responses;
using MeetingAssistant.Shared.Abstractions;

namespace MeetingAssistant.Features.Meetings.Services
{
    public interface IRecurrenceService
    {
        Task<Result<RecurringMeetingCreationResponse>> GenerateMeetingInstancesAsync(
            Guid userId,
            CreateRecurringMeetingRequest request,
            CancellationToken cancellationToken = default);

        Task<Result<RecurringSeriesListResponse>> ListRecurringSeriesAsync(
            int page,
            int pageSize,
            CancellationToken cancellationToken = default);

        Task<Result<RecurringSeriesResponse>> GetRecurringSeriesAsync(
            Guid seriesId,
            CancellationToken cancellationToken = default);

        Task<Result<RecurringSeriesResponse>> UpdateRecurringSeriesAsync(
            Guid seriesId,
            Guid userId,
            UpdateRecurringSeriesRequest request,
            CancellationToken cancellationToken = default);

        Task<Result<RecurringSeriesResponse>> DeleteRecurringSeriesAsync(
            Guid seriesId,
            Guid userId,
            CancellationToken cancellationToken = default);
    }
}
