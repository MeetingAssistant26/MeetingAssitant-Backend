using Livekit.Server.Sdk.Dotnet;
using MeetingAssistant.Features.LiveSession.Infrastructure;
using MeetingAssistant.Features.LiveSession.Models;
using MeetingAssistant.Features.Meetings.Models;
using MeetingAssistant.Shared.Abstractions;
using MeetingAssistant.Shared.Errors;
using Microsoft.Extensions.Options;

namespace MeetingAssistant.Features.LiveSession.Services
{
    public class LiveKitTokenIssuer(
        IOptions<LiveKitOptions> options,
        ILogger<LiveKitTokenIssuer> logger) : ILiveKitTokenIssuer
    {
        private readonly LiveKitOptions _options = options.Value;
        private readonly ILogger<LiveKitTokenIssuer> _logger = logger;

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

                _logger.LogInformation(
                    "Issued LiveKit token. MeetingId={MeetingId} UserId={UserId} RoleAtIssuance={RoleAtIssuance} ExpiresAtUtc={ExpiresAtUtc}",
                    meetingId,
                    userId,
                    role,
                    expiresAtUtc);

                return Result.Success(new IssuedJoinToken(jwt, roomName, _options.ServerUrl, expiresAtUtc));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "LiveKit token issuance failed. MeetingId={MeetingId} UserId={UserId}",
                    meetingId,
                    userId);
                return Result.Failure<IssuedJoinToken>(LiveSessionErrors.LiveKitCallFailed);
            }
        }
    }
}
