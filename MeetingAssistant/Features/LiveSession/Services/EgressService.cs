using Livekit.Server.Sdk.Dotnet;
using MeetingAssistant.Features.LiveSession.Infrastructure;
using Microsoft.Extensions.Options;

// Egress writes directly to S3 (MinIO) via the egress service's configured default storage.
// Filepath values here become S3 object keys; the egress container handles the actual upload.

namespace MeetingAssistant.Features.LiveSession.Services
{
    public class EgressService : IEgressService
    {
        private readonly LiveKitOptions _options;
        private readonly ILogger<EgressService> _logger;
        private readonly EgressServiceClient _client;

        public EgressService(
            IOptions<LiveKitOptions> options,
            IHttpClientFactory httpClientFactory,
            ILogger<EgressService> logger)
        {
            _options = options.Value;
            _logger = logger;
            var httpClient = httpClientFactory.CreateClient();
            _client = new EgressServiceClient(_options.EgressHost, _options.ApiKey, _options.ApiSecret, httpClient);
        }

        public async Task<EgressStartResult> StartTrackEgressAsync(
            Guid meetingId,
            string roomName,
            string trackId,
            string participantIdentity,
            CancellationToken ct = default)
        {
            var safeRoomName = SanitizePathSegment(roomName);
            var safeIdentity = SanitizePathSegment(participantIdentity);
            var storageObjectKey = $"tracks/{safeRoomName}/{safeIdentity}/track-{trackId}.ogg";

            var request = new TrackEgressRequest
            {
                RoomName = roomName,
                TrackId = trackId,
                File = new DirectFileOutput
                {
                    Filepath = storageObjectKey,
                    DisableManifest = true
                }
            };

            var result = await _client.StartTrackEgress(request);
            _logger.LogInformation(
                "Started track egress. MeetingId={MeetingId} RoomName={RoomName} TrackId={TrackId} EgressId={EgressId} StorageObjectKey={StorageObjectKey}",
                meetingId,
                roomName,
                trackId,
                result.EgressId,
                storageObjectKey);

            return new EgressStartResult(result.EgressId, storageObjectKey);
        }

        private static string SanitizePathSegment(string segment)
        {
            return segment.Replace('/', '_').Replace('\\', '_');
        }
    }
}
