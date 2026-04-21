using Livekit.Server.Sdk.Dotnet;
using MeetingAssistant.Features.LiveSession.Infrastructure;
using MeetingAssistant.Features.LiveSession.Models;
using MeetingAssistant.Features.Meetings.Models;
using MeetingAssistant.Shared.Abstractions;
using MeetingAssistant.Shared.Errors;
using Microsoft.Extensions.Options;

namespace MeetingAssistant.Features.LiveSession.Services
{
    public class LiveKitTokenIssuer(IOptions<LiveKitOptions> options) : ILiveKitTokenIssuer
    {
        private readonly LiveKitOptions _options = options.Value;

        public Result<IssuedJoinToken> Issue(
            Guid meetingId,
            Guid organizationId,
            Guid userId,
            string displayName,
            MeetingRole role,
            SessionPermissions permissions)
        {
            if (string.IsNullOrWhiteSpace(_options.ApiKey)
                || string.IsNullOrWhiteSpace(_options.ApiSecret)
                || string.IsNullOrWhiteSpace(_options.ServerUrl))
            {
                return Result.Failure<IssuedJoinToken>(LiveSessionErrors.LiveKitCallFailed);
            }

            try
            {
                var roomName = $"mtg:{meetingId}";
                var expiresAtUtc = DateTime.UtcNow.AddMinutes(15);

                var accessToken = new AccessToken(_options.ApiKey, _options.ApiSecret)
                    .WithIdentity($"user:{userId}")
                    .WithName(displayName)
                    .WithGrants(new VideoGrants
                    {
                        RoomJoin = true,
                        Room = roomName,
                        CanPublish = permissions.CanPublish,
                        CanSubscribe = permissions.CanSubscribe,
                        CanPublishData = permissions.CanPublishData,
                        RoomAdmin = permissions.CanModerate
                    })
                    .WithAttributes(new Dictionary<string, string>
                    {
                        ["organizationId"] = organizationId.ToString(),
                        ["meetingRole"] = role.ToString()
                    })
                    .WithTtl(TimeSpan.FromMinutes(15));

                var jwt = accessToken.ToJwt();

                return Result.Success(new IssuedJoinToken(jwt, roomName, _options.ServerUrl, expiresAtUtc));
            }
            catch
            {
                return Result.Failure<IssuedJoinToken>(LiveSessionErrors.LiveKitCallFailed);
            }
        }
    }
}
