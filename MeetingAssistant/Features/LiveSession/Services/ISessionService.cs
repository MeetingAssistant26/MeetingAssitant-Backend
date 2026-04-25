using MeetingAssistant.Features.LiveSession.Contracts.Responses;
using MeetingAssistant.Shared.Abstractions;

namespace MeetingAssistant.Features.LiveSession.Services
{
    public interface ISessionService
    {
        Task<Result<JoinTokenResponse>> IssueJoinTokenAsync(
            Guid meetingId,
            Guid callerUserId,
            string? displayName,
            CancellationToken cancellationToken = default);
    }
}
