using MeetingAssistant.Features.LiveSession.Models;

namespace MeetingAssistant.Features.LiveSession.Contracts.Responses
{
    public sealed record JoinTokenResponse(
        string AccessToken,
        string RoomName,
        string ServerUrl,
        DateTime ExpiresAtUtc,
        SessionPermissions Permissions);
}
