using Hangfire;
using Livekit.Server.Sdk.Dotnet;
using MeetingAssistant.Features.LiveSession.Infrastructure;
using MeetingAssistant.Features.LiveSession.Jobs;
using MeetingAssistant.Features.LiveSession.Models;
using MeetingAssistant.Features.LiveSession.Models.Events;
using MeetingAssistant.Features.Meetings.Models.Events;
using MeetingAssistant.Infrastructure.Persistence.DbContext;
using MeetingAssistant.Shared.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using System.Text.Json;

namespace MeetingAssistant.Features.LiveSession.Services
{
    public class WebhookService(
        ApplicationDbContext dbContext,
        IBackgroundJobClient backgroundJobClient,
        IOptions<LiveKitOptions> options,
        IEgressService egressService,
        ILogger<WebhookService> logger) : IWebhookService
    {
        private const string UniqueViolationSqlState = "23505";

        private readonly ApplicationDbContext _dbContext = dbContext;
        private readonly IBackgroundJobClient _backgroundJobClient = backgroundJobClient;
        private readonly LiveKitOptions _options = options.Value;
        private readonly IEgressService _egressService = egressService;
        private readonly ILogger<WebhookService> _logger = logger;

        public async Task<Result> ProcessAsync(
            WebhookEvent webhookEvent,
            string rawPayload,
            CancellationToken cancellationToken = default)
        {
            var eventType = MapEventType(webhookEvent.Event);

            if (!TryResolveMeetingId(webhookEvent, out var meetingId))
            {
                _logger.LogWarning(
                    "LiveKit webhook ignored because room name was not resolvable. Event={Event} EventId={EventId}",
                    webhookEvent.Event,
                    webhookEvent.Id);
                return Result.Success();
            }

            var meeting = await _dbContext.Meetings
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(m => m.Id == meetingId, cancellationToken);

            if (meeting == null)
            {
                _logger.LogWarning(
                    "LiveKit webhook ignored because no meeting matched room for Event={Event} EventId={EventId}",
                    webhookEvent.Event,
                    webhookEvent.Id);
                return Result.Success();
            }

            var externalEventId = ResolveExternalEventId(webhookEvent, eventType, meetingId);
            var alreadyProcessed = await _dbContext.SessionEvents
                .IgnoreQueryFilters()
                .AnyAsync(x => x.ExternalEventId == externalEventId, cancellationToken);

            if (alreadyProcessed)
            {
                return Result.Success();
            }

            var occurredAtUtc = ResolveOccurredAtUtc(webhookEvent.CreatedAt);
            var participantUserId = await ResolveParticipantUserIdAsync(
                meetingId,
                webhookEvent.Participant?.Identity,
                cancellationToken);

            var sessionEvent = new SessionEvent
            {
                MeetingId = meeting.Id,
                OrganizationId = meeting.OrganizationId,
                ExternalEventId = externalEventId,
                EventType = eventType,
                ParticipantUserId = participantUserId,
                PayloadJson = rawPayload,
                OccurredAtUtc = occurredAtUtc,
                ProcessedAtUtc = DateTime.UtcNow
            };

            _dbContext.SessionEvents.Add(sessionEvent);

            var ingestEnqueues = new List<(Guid TrackId, string S3LocationUrl, long? SizeBytes)>();

            switch (eventType)
            {
                case SessionEventType.RoomStarted:
                    meeting.RoomActivatedAtUtc ??= occurredAtUtc;

                    if (meeting.Status == Features.Meetings.Models.MeetingStatus.Scheduled)
                    {
                        meeting.Status = Features.Meetings.Models.MeetingStatus.InProgress;
                        meeting.RaiseDomainEvent(new MeetingStartedEvent(meeting.OrganizationId, meeting.Id));
                    }
                    break;

                case SessionEventType.RoomFinished:
                    if (meeting.Status != Features.Meetings.Models.MeetingStatus.Completed)
                    {
                        meeting.Status = Features.Meetings.Models.MeetingStatus.Completed;
                        meeting.RaiseDomainEvent(new MeetingEndedEvent(meeting.OrganizationId, meeting.Id));
                    }
                    break;

                case SessionEventType.EgressEnded:
                {
                    // For per-participant audio capture, each egress (Participant or Track mode)
                    // targets exactly one participant. The identity is on the egress request, not
                    // on FileResult — fileResults[].participantIdentity is not part of LiveKit's
                    // schema. See livekit-sandbox-runbook.md for the required egress configuration.
                    var fileResults = webhookEvent.EgressInfo?.FileResults ?? [];
                    var egressIdentity = ExtractEgressParticipantIdentity(rawPayload);
                    var egressOk = webhookEvent.EgressInfo?.Status == EgressStatus.EgressComplete;

                    var resolvedParticipantUserId = await ResolveParticipantUserIdAsync(
                        meeting.Id,
                        egressIdentity,
                        cancellationToken);

                    if (resolvedParticipantUserId is null)
                    {
                        _logger.LogWarning(
                            "Skipping egress payload because participant identity was not resolvable. MeetingId={MeetingId} Identity={Identity} FileResultCount={FileResultCount}",
                            meeting.Id,
                            egressIdentity,
                            fileResults.Count);
                        break;
                    }

                    foreach (var file in fileResults)
                    {
                        if (!string.IsNullOrWhiteSpace(file.Location)
                            && !file.Location.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase))
                        {
                            _logger.LogWarning(
                                "Skipping non-audio file in egress payload. MeetingId={MeetingId} ParticipantUserId={ParticipantUserId} FileLocation={FileLocation}",
                                meeting.Id,
                                resolvedParticipantUserId.Value,
                                file.Location);
                            continue;
                        }

                        var status = egressOk && !string.IsNullOrWhiteSpace(file.Location)
                            ? ParticipantAudioTrackStatus.Pending
                            : ParticipantAudioTrackStatus.Failed;

                        var trackId = await UpsertParticipantAudioTrackAsync(
                            meeting.Id,
                            meeting.OrganizationId,
                            resolvedParticipantUserId.Value,
                            status,
                            cancellationToken);

                        if (status == ParticipantAudioTrackStatus.Pending)
                        {
                            ingestEnqueues.Add((trackId, file.Location!, file.Size));
                        }
                    }
                    break;
                }

                case SessionEventType.ParticipantJoined:
                case SessionEventType.ParticipantLeft:
                    // SessionEvent already persisted before the switch; no additional side effects.
                    break;

                case SessionEventType.TrackPublished:
                    if (webhookEvent.Track?.Type == TrackType.Audio &&
                        webhookEvent.Track?.Source == TrackSource.Microphone &&
                        !string.IsNullOrWhiteSpace(_options.EgressHost))
                    {
                        var trackSid = webhookEvent.Track?.Sid;
                        var identity = webhookEvent.Participant?.Identity;
                        if (!string.IsNullOrWhiteSpace(trackSid) && !string.IsNullOrWhiteSpace(identity))
                        {
                            _ = _egressService.StartTrackEgressAsync(
                                meeting.Id,
                                webhookEvent.Room?.Name ?? $"mtg:{meeting.Id}",
                                trackSid,
                                identity,
                                cancellationToken);
                        }
                    }
                    break;

                case SessionEventType.Unknown:
                default:
                    break;
            }

            try
            {
                await _dbContext.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException ex) when (IsUniqueViolation(ex))
            {
                return Result.Success();
            }

            foreach (var (trackId, s3LocationUrl, sizeBytes) in ingestEnqueues)
            {
                _backgroundJobClient.Enqueue<IngestParticipantAudioJob>(
                    job => job.RunAsync(trackId, s3LocationUrl, sizeBytes, CancellationToken.None));
            }

            return Result.Success();
        }

        private async Task<Guid> UpsertParticipantAudioTrackAsync(
            Guid meetingId,
            Guid organizationId,
            Guid participantUserId,
            ParticipantAudioTrackStatus status,
            CancellationToken cancellationToken)
        {
            var track = await _dbContext.ParticipantAudioTracks
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(
                    x => x.MeetingId == meetingId && x.ParticipantUserId == participantUserId,
                    cancellationToken);

            if (track == null)
            {
                track = new ParticipantAudioTrack
                {
                    MeetingId = meetingId,
                    OrganizationId = organizationId,
                    ParticipantUserId = participantUserId,
                    Status = status
                };

                _dbContext.ParticipantAudioTracks.Add(track);
                return track.Id;
            }

            track.Status = status;
            return track.Id;
        }

        private static SessionEventType MapEventType(string? eventName)
        {
            return eventName?.ToLowerInvariant() switch
            {
                "room_started" => SessionEventType.RoomStarted,
                "room_finished" => SessionEventType.RoomFinished,
                "participant_joined" => SessionEventType.ParticipantJoined,
                "participant_left" => SessionEventType.ParticipantLeft,
                "recording_started" => SessionEventType.RecordingStarted,
                "track_published" => SessionEventType.TrackPublished,
                "egress_ended" => SessionEventType.EgressEnded,
                _ => SessionEventType.Unknown
            };
        }

        private static DateTime ResolveOccurredAtUtc(long createdAtUnixSeconds)
        {
            return createdAtUnixSeconds > 0
                ? DateTimeOffset.FromUnixTimeSeconds(createdAtUnixSeconds).UtcDateTime
                : DateTime.UtcNow;
        }

        private static string ResolveExternalEventId(WebhookEvent webhookEvent, SessionEventType eventType, Guid meetingId)
        {
            if (!string.IsNullOrWhiteSpace(webhookEvent.Id))
            {
                return webhookEvent.Id;
            }

            return $"{eventType}:{meetingId}:{webhookEvent.CreatedAt}";
        }

        private static bool TryResolveMeetingId(WebhookEvent webhookEvent, out Guid meetingId)
        {
            meetingId = Guid.Empty;

            var roomName = webhookEvent.Room?.Name;
            if (!string.IsNullOrWhiteSpace(roomName)
                && TryParseMeetingId(roomName, out meetingId))
            {
                return true;
            }

            roomName = webhookEvent.EgressInfo?.RoomName;
            if (!string.IsNullOrWhiteSpace(roomName)
                && TryParseMeetingId(roomName, out meetingId))
            {
                return true;
            }

            return false;
        }

        private static bool TryParseMeetingId(string roomName, out Guid meetingId)
        {
            meetingId = Guid.Empty;

            const string prefix = "mtg:";
            if (!roomName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var idPart = roomName[prefix.Length..];
            return Guid.TryParse(idPart, out meetingId);
        }

        private static string? ExtractEgressParticipantIdentity(string rawPayload)
        {
            if (string.IsNullOrWhiteSpace(rawPayload))
            {
                return null;
            }

            try
            {
                using var document = JsonDocument.Parse(rawPayload);
                if (!document.RootElement.TryGetProperty("egressInfo", out var egressInfo)
                    || egressInfo.ValueKind != JsonValueKind.Object)
                {
                    return null;
                }

                // TrackEgress ended webhooks do not include a participant identity field.
                // We use the S3 object key path to resolve the participant.
                // Expected path format: tracks/{roomName}/{participantIdentity}/track-{trackId}.ogg
                if (egressInfo.TryGetProperty("fileResults", out var fileResults)
                    && fileResults.ValueKind == JsonValueKind.Array
                    && fileResults.GetArrayLength() > 0)
                {
                    var firstFile = fileResults[0];
                    var path = firstFile.TryGetProperty("filename", out var filename)
                        ? filename.GetString()
                        : firstFile.TryGetProperty("location", out var location)
                            ? new Uri(location.GetString()!).AbsolutePath
                            : null;

                    if (!string.IsNullOrWhiteSpace(path))
                    {
                        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
                        // tracks/room/user:{guid}/file.ogg → identity is index 2
                        if (segments.Length >= 3)
                        {
                            var identity = segments[2];
                            if (!string.IsNullOrWhiteSpace(identity))
                            {
                                return identity;
                            }
                        }
                    }
                }

                return null;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private async Task<Guid?> ResolveParticipantUserIdAsync(
            Guid meetingId,
            string? participantIdentity,
            CancellationToken cancellationToken)
        {
            if (!TryParseParticipantIdentity(participantIdentity, out var userId))
            {
                return null;
            }

            var exists = await _dbContext.MeetingParticipants
                .IgnoreQueryFilters()
                .AnyAsync(x => x.MeetingId == meetingId && x.UserId == userId, cancellationToken);

            return exists ? userId : null;
        }

        private static bool TryParseParticipantIdentity(string? identity, out Guid userId)
        {
            userId = Guid.Empty;
            if (string.IsNullOrWhiteSpace(identity))
            {
                return false;
            }

            const string prefix = "user:";
            if (!identity.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return Guid.TryParse(identity[prefix.Length..], out userId);
        }

        private static bool IsUniqueViolation(DbUpdateException exception)
        {
            return exception.InnerException is PostgresException postgresException
                && postgresException.SqlState == UniqueViolationSqlState;
        }
    }
}
