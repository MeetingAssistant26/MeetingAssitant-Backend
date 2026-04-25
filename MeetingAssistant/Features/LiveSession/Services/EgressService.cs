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
        private readonly IStorageService _storage;
        private readonly ILogger<EgressService> _logger;
        private readonly EgressServiceClient _client;

        public EgressService(
            IOptions<LiveKitOptions> options,
            IStorageService storage,
            IHttpClientFactory httpClientFactory,
            ILogger<EgressService> logger)
        {
            _options = options.Value;
            _storage = storage;
            _logger = logger;
            var httpClient = httpClientFactory.CreateClient();
            _client = new EgressServiceClient(_options.EgressHost, _options.ApiKey, _options.ApiSecret, httpClient);
        }

        public async Task StartTrackEgressAsync(
            Guid meetingId,
            string roomName,
            string trackId,
            string participantIdentity,
            CancellationToken ct = default)
        {
            await _storage.EnsureBucketExistsAsync(ct);

            var safeRoomName = SanitizePathSegment(roomName);
            var safeIdentity = SanitizePathSegment(participantIdentity);

            var request = new TrackEgressRequest
            {
                RoomName = roomName,
                TrackId = trackId,
                File = new DirectFileOutput
                {
                    Filepath = $"tracks/{safeRoomName}/{safeIdentity}/track-{trackId}.ogg",
                    DisableManifest = true
                }
            };

            var result = await _client.StartTrackEgress(request);
            _logger.LogInformation(
                "Started track egress. MeetingId={MeetingId} RoomName={RoomName} TrackId={TrackId} EgressId={EgressId}",
                meetingId,
                roomName,
                trackId,
                result.EgressId);
        }

        private static string SanitizePathSegment(string segment)
        {
            return segment.Replace('/', '_').Replace('\\', '_');
        }
    }
}
