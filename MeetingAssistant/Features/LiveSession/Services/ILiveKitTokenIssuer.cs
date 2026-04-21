using MeetingAssistant.Features.LiveSession.Models;
using MeetingAssistant.Features.Meetings.Models;
using MeetingAssistant.Shared.Abstractions;

namespace MeetingAssistant.Features.LiveSession.Services
{
    public sealed record IssuedJoinToken(
        string AccessToken,
        string RoomName,
        string ServerUrl,
        DateTime ExpiresAtUtc);

    public interface ILiveKitTokenIssuer
    {
        Result<IssuedJoinToken> Issue(
            Guid meetingId,
            Guid organizationId,
            Guid userId,
            string displayName,
            MeetingRole role,
            SessionPermissions permissions);
    }
}
