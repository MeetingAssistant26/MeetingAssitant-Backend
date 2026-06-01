using MeetingAssistant.Features.LiveSession.Contracts.Responses;
using MeetingAssistant.Shared.Abstractions;

namespace MeetingAssistant.Features.LiveSession.Services
{
    public interface IAiAssistantDispatchService
    {
        Task<Result<AiAssistantStatusResponse>> GetStatusAsync(
            Guid organizationId,
            Guid meetingId,
            Guid callerUserId,
            CancellationToken cancellationToken = default);

        Task<Result<AiAssistantStatusResponse>> EnableAsync(
            Guid organizationId,
            Guid meetingId,
            Guid callerUserId,
            CancellationToken cancellationToken = default);

        Task<Result<AiAssistantStatusResponse>> DisableAsync(
            Guid organizationId,
            Guid meetingId,
            Guid callerUserId,
            CancellationToken cancellationToken = default);

        Task<Result<AiAssistantStatusResponse>> EnsureDispatchedForMeetingAsync(
            Guid organizationId,
            Guid meetingId,
            Guid? requestedByUserId = null,
            CancellationToken cancellationToken = default);
    }
}
