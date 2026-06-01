using MeetingAssistant.Features.Meetings.Contracts.Requests;
using MeetingAssistant.Features.Meetings.Contracts.Responses;
using MeetingAssistant.Shared.Abstractions;

namespace MeetingAssistant.Features.Meetings.Services
{
    public interface IMeetingService
    {
        Task<Result<MeetingResponse>> CreateMeetingAsync(
            CreateMeetingRequest request,
            Guid userId,
            CancellationToken cancellationToken = default);

        Task<Result<MeetingResponse>> UpdateMeetingAsync(
            Guid meetingId,
            UpdateMeetingRequest request,
            Guid userId,
            CancellationToken cancellationToken = default);

        Task<Result> CancelMeetingAsync(
            Guid meetingId,
            Guid userId,
            CancellationToken cancellationToken = default);

        Task<Result<MeetingListResponse>> ListMeetingsAsync(
            string? filter,
            int page,
            int pageSize,
            CancellationToken cancellationToken = default);

        Task<Result<MeetingResponse>> GetMeetingAsync(
            Guid meetingId,
            CancellationToken cancellationToken = default);
    }
}
