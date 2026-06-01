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
using System.Security.Cryptography;
using System.Text;
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

            var ingestEnqueues = new List<(Guid FragmentId, string S3LocationUrl, long? SizeBytes)>();
            var egressStarts = new List<(Guid MeetingId, Guid FragmentId, string RoomName, string TrackSid, string ParticipantIdentity)>();

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
                    var fileResults = webhookEvent.EgressInfo?.FileResults;
                    var egressIdentity = ExtractEgressParticipantIdentity(rawPayload);
                    var egressOk = webhookEvent.EgressInfo?.Status == EgressStatus.EgressComplete;
                    var egressDetails = ExtractEgressDetails(rawPayload);

                    if (fileResults is { Count: > 0 })
                    {
                        foreach (var file in fileResults)
                        {
                            await ProcessEgressFileResultAsync(file);
                        }
                    }
                    else if (!egressOk)
                    {
                        await ProcessEgressFileResultAsync(null);
                    }

                    async Task ProcessEgressFileResultAsync(Livekit.Server.Sdk.Dotnet.FileInfo? file)
                    {
                        var fileLocation = file?.Location;
                        var fileName = file?.Filename;

                        if (!IsAudioEgressFile(fileName, fileLocation))
                        {
                            _logger.LogWarning(
                                "Skipping non-audio file in egress payload. MeetingId={MeetingId} ParticipantUserId={ParticipantUserId} FileLocation={FileLocation}",
                                meeting.Id,
                                null,
                                fileLocation);
                            return;
                        }

                        var trackSid = ResolveEgressTrackSid(egressDetails.TrackSid, fileName, fileLocation, egressDetails.EgressId);
                        var existingFragment = await FindParticipantAudioFragmentAsync(
                            meeting.Id,
                            trackSid,
                            egressDetails.EgressId,
                            fileLocation,
                            cancellationToken);

                        var fileIdentity = ExtractParticipantIdentityFromEgressPath(fileName)
                            ?? ExtractParticipantIdentityFromEgressPath(fileLocation)
                            ?? egressIdentity;

                        var resolvedParticipantUserId = existingFragment?.ParticipantUserId
                            ?? await ResolveParticipantUserIdAsync(
                                meeting.Id,
                                fileIdentity,
                                cancellationToken);

                        if (resolvedParticipantUserId is null)
                        {
                            _logger.LogWarning(
                                "Skipping egress file because participant identity was not resolvable. MeetingId={MeetingId} Identity={Identity} FileLocation={FileLocation} TrackSid={TrackSid} EgressId={EgressId}",
                                meeting.Id,
                                fileIdentity,
                                fileLocation,
                                trackSid,
                                egressDetails.EgressId);
                            return;
                        }

                        var status = egressOk && !string.IsNullOrWhiteSpace(fileLocation)
                            ? ParticipantAudioFragmentStatus.Pending
                            : ParticipantAudioFragmentStatus.Failed;

                        var aggregateStatus = status == ParticipantAudioFragmentStatus.Failed
                            ? ParticipantAudioTrackStatus.Failed
                            : ParticipantAudioTrackStatus.Pending;

                        var trackId = await UpsertParticipantAudioTrackAsync(
                            meeting.Id,
                            meeting.OrganizationId,
                            resolvedParticipantUserId.Value,
                            aggregateStatus,
                            cancellationToken);

                        var fragment = await UpsertParticipantAudioFragmentAsync(
                            meeting.Id,
                            meeting.OrganizationId,
                            resolvedParticipantUserId.Value,
                            trackId,
                            trackSid,
                            egressDetails.EgressId,
                            fileName,
                            fileLocation,
                            ExtractObjectKeyFromS3Url(fileLocation),
                            status,
                            file?.Size,
                            existingFragment,
                            trackPublishedAtUtc: null,
                            egressStartedAtUtc: egressDetails.StartedAtUtc,
                            egressEndedAtUtc: egressDetails.EndedAtUtc ?? occurredAtUtc,
                            failureCode: status == ParticipantAudioFragmentStatus.Failed ? egressDetails.FailureCode ?? "egress_failed" : null,
                            failureMessage: status == ParticipantAudioFragmentStatus.Failed ? egressDetails.FailureMessage ?? $"LiveKit egress ended with status {webhookEvent.EgressInfo?.Status}" : null,
                            cancellationToken);

                        if (status == ParticipantAudioFragmentStatus.Failed)
                        {
                            await RefreshParticipantAudioTrackAggregateAsync(trackId, cancellationToken);
                        }

                        if (status == ParticipantAudioFragmentStatus.Pending)
                        {
                            ingestEnqueues.Add((fragment.Id, fileLocation!, file?.Size));
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
                            var resolvedParticipantUserId = participantUserId;
                            if (resolvedParticipantUserId is null)
                            {
                                _logger.LogWarning(
                                    "Skipping track egress because participant identity was not resolvable. MeetingId={MeetingId} Identity={Identity} TrackSid={TrackSid}",
                                    meeting.Id,
                                    identity,
                                    trackSid);
                                break;
                            }

                            var trackId = await UpsertParticipantAudioTrackAsync(
                                meeting.Id,
                                meeting.OrganizationId,
                                resolvedParticipantUserId.Value,
                                ParticipantAudioTrackStatus.Pending,
                                cancellationToken);

                            var fragment = await UpsertParticipantAudioFragmentAsync(
                                meeting.Id,
                                meeting.OrganizationId,
                                resolvedParticipantUserId.Value,
                                trackId,
                                trackSid,
                                egressId: null,
                                fileName: null,
                                storageLocation: null,
                                storageObjectKey: null,
                                status: ParticipantAudioFragmentStatus.Pending,
                                sizeBytes: null,
                                existingFragment: null,
                                trackPublishedAtUtc: occurredAtUtc,
                                egressStartedAtUtc: null,
                                egressEndedAtUtc: null,
                                failureCode: null,
                                failureMessage: null,
                                cancellationToken);

                            egressStarts.Add((
                                meeting.Id,
                                fragment.Id,
                                webhookEvent.Room?.Name ?? $"mtg:{meeting.Id}",
                                trackSid,
                                identity));
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

            foreach (var (fragmentId, s3LocationUrl, sizeBytes) in ingestEnqueues)
            {
                _backgroundJobClient.Enqueue<IngestParticipantAudioJob>(
                    job => job.RunFragmentAsync(fragmentId, s3LocationUrl, sizeBytes, CancellationToken.None));
            }

            foreach (var egressStart in egressStarts)
            {
                try
                {
                    await _egressService.StartTrackEgressAsync(
                        egressStart.MeetingId,
                        egressStart.RoomName,
                        egressStart.TrackSid,
                        egressStart.ParticipantIdentity,
                        cancellationToken);
                }
                catch (Exception ex)
                {
                    await MarkFragmentFailedAsync(
                        egressStart.FragmentId,
                        "egress_start_failed",
                        ex.Message,
                        cancellationToken);

                    _logger.LogError(
                        ex,
                        "Failed to start track egress. MeetingId={MeetingId} TrackSid={TrackSid} ParticipantIdentity={ParticipantIdentity}",
                        egressStart.MeetingId,
                        egressStart.TrackSid,
                        egressStart.ParticipantIdentity);
                }
            }

            return Result.Success();
        }

        private async Task<ParticipantAudioFragment?> FindParticipantAudioFragmentAsync(
            Guid meetingId,
            string? trackSid,
            string? egressId,
            string? storageLocation,
            CancellationToken cancellationToken)
        {
            if (!string.IsNullOrWhiteSpace(trackSid))
            {
                var byTrackSid = await _dbContext.ParticipantAudioFragments
                    .IgnoreQueryFilters()
                    .FirstOrDefaultAsync(
                        x => x.MeetingId == meetingId && x.TrackSid == trackSid,
                        cancellationToken);

                if (byTrackSid is not null)
                {
                    return byTrackSid;
                }
            }

            if (!string.IsNullOrWhiteSpace(egressId))
            {
                var byEgressId = await _dbContext.ParticipantAudioFragments
                    .IgnoreQueryFilters()
                    .FirstOrDefaultAsync(
                        x => x.MeetingId == meetingId && x.EgressId == egressId,
                        cancellationToken);

                if (byEgressId is not null)
                {
                    return byEgressId;
                }
            }

            if (!string.IsNullOrWhiteSpace(storageLocation))
            {
                return await _dbContext.ParticipantAudioFragments
                    .IgnoreQueryFilters()
                    .FirstOrDefaultAsync(
                        x => x.MeetingId == meetingId && x.StorageLocation == storageLocation,
                        cancellationToken);
            }

            return null;
        }

        private async Task<ParticipantAudioFragment> UpsertParticipantAudioFragmentAsync(
            Guid meetingId,
            Guid organizationId,
            Guid participantUserId,
            Guid participantAudioTrackId,
            string? trackSid,
            string? egressId,
            string? fileName,
            string? storageLocation,
            string? storageObjectKey,
            ParticipantAudioFragmentStatus status,
            long? sizeBytes,
            ParticipantAudioFragment? existingFragment,
            DateTime? trackPublishedAtUtc,
            DateTime? egressStartedAtUtc,
            DateTime? egressEndedAtUtc,
            string? failureCode,
            string? failureMessage,
            CancellationToken cancellationToken)
        {
            var effectiveTrackSid = trackSid
                ?? ResolveEgressTrackSid(null, fileName, storageLocation, egressId)
                ?? $"egress:{HashExternalEventSignature($"{meetingId}:{participantUserId}:{egressId}:{storageLocation}:{fileName}")}";

            var fragment = existingFragment ?? await _dbContext.ParticipantAudioFragments
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(
                    x => x.MeetingId == meetingId && x.TrackSid == effectiveTrackSid,
                    cancellationToken);

            if (fragment is null)
            {
                fragment = new ParticipantAudioFragment
                {
                    MeetingId = meetingId,
                    OrganizationId = organizationId,
                    ParticipantUserId = participantUserId,
                    ParticipantAudioTrackId = participantAudioTrackId,
                    TrackSid = effectiveTrackSid
                };

                _dbContext.ParticipantAudioFragments.Add(fragment);
            }

            fragment.OrganizationId = organizationId;
            fragment.ParticipantUserId = participantUserId;
            fragment.ParticipantAudioTrackId = participantAudioTrackId;
            fragment.EgressId = string.IsNullOrWhiteSpace(egressId) ? fragment.EgressId : egressId;
            fragment.StorageLocation = string.IsNullOrWhiteSpace(storageLocation) ? fragment.StorageLocation : storageLocation;
            fragment.StorageObjectKey = string.IsNullOrWhiteSpace(storageObjectKey) ? fragment.StorageObjectKey : storageObjectKey;
            fragment.SizeBytes = sizeBytes ?? fragment.SizeBytes;
            fragment.TrackPublishedAtUtc ??= trackPublishedAtUtc;
            fragment.EgressStartedAtUtc = egressStartedAtUtc ?? fragment.EgressStartedAtUtc;
            fragment.EgressEndedAtUtc = egressEndedAtUtc ?? fragment.EgressEndedAtUtc;

            if (status == ParticipantAudioFragmentStatus.Failed)
            {
                fragment.Status = ParticipantAudioFragmentStatus.Failed;
                fragment.FailedAtUtc ??= DateTime.UtcNow;
                fragment.FailureCode = failureCode ?? fragment.FailureCode;
                fragment.FailureMessage = failureMessage ?? fragment.FailureMessage;
            }
            else if (fragment.Status != ParticipantAudioFragmentStatus.Available)
            {
                fragment.Status = status;
                fragment.FailedAtUtc = null;
                fragment.FailureCode = null;
                fragment.FailureMessage = null;
            }

            return fragment;
        }

        private async Task MarkFragmentFailedAsync(
            Guid fragmentId,
            string failureCode,
            string failureMessage,
            CancellationToken cancellationToken)
        {
            var fragment = await _dbContext.ParticipantAudioFragments
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(x => x.Id == fragmentId, cancellationToken);

            if (fragment is null)
            {
                return;
            }

            fragment.Status = ParticipantAudioFragmentStatus.Failed;
            fragment.FailedAtUtc = DateTime.UtcNow;
            fragment.FailureCode = failureCode;
            fragment.FailureMessage = failureMessage;

            if (fragment.ParticipantAudioTrackId is { } trackId)
            {
                await RefreshParticipantAudioTrackAggregateAsync(trackId, cancellationToken);
            }

            await _dbContext.SaveChangesAsync(cancellationToken);
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

        private async Task RefreshParticipantAudioTrackAggregateAsync(
            Guid participantAudioTrackId,
            CancellationToken cancellationToken)
        {
            var track = await _dbContext.ParticipantAudioTracks
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(x => x.Id == participantAudioTrackId, cancellationToken);

            if (track is null)
            {
                return;
            }

            var fragments = await _dbContext.ParticipantAudioFragments
                .IgnoreQueryFilters()
                .Where(x => x.ParticipantAudioTrackId == participantAudioTrackId)
                .ToListAsync(cancellationToken);

            if (fragments.Count == 0)
            {
                return;
            }

            if (fragments.Any(x => x.Status == ParticipantAudioFragmentStatus.Pending))
            {
                track.Status = ParticipantAudioTrackStatus.Pending;
                return;
            }

            var latestAvailable = fragments
                .Where(x => x.Status == ParticipantAudioFragmentStatus.Available)
                .OrderByDescending(x => x.EgressEndedAtUtc ?? x.StorageAvailableAtUtc ?? x.TrackPublishedAtUtc ?? x.UpdatedAtUtc)
                .FirstOrDefault();

            if (latestAvailable is not null)
            {
                track.Status = ParticipantAudioTrackStatus.Available;
                track.StorageObjectKey = latestAvailable.StorageObjectKey;
                track.SizeBytes = latestAvailable.SizeBytes;
                track.DurationSeconds = latestAvailable.DurationSeconds;
                return;
            }

            if (fragments.All(x => x.Status == ParticipantAudioFragmentStatus.Failed))
            {
                track.Status = ParticipantAudioTrackStatus.Failed;
            }
        }

        private static bool IsAudioEgressFile(string? fileName, string? fileLocation)
        {
            var candidate = !string.IsNullOrWhiteSpace(fileLocation) ? fileLocation : fileName;
            if (string.IsNullOrWhiteSpace(candidate))
            {
                return true;
            }

            return candidate.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase)
                || candidate.EndsWith(".wav", StringComparison.OrdinalIgnoreCase)
                || candidate.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase)
                || candidate.EndsWith(".m4a", StringComparison.OrdinalIgnoreCase)
                || candidate.EndsWith(".opus", StringComparison.OrdinalIgnoreCase);
        }

        private static string? ResolveEgressTrackSid(
            string? egressTrackSid,
            string? fileName,
            string? fileLocation,
            string? egressId)
        {
            return FirstNonEmpty(
                egressTrackSid,
                ExtractTrackSidFromEgressPath(fileName),
                ExtractTrackSidFromEgressPath(fileLocation),
                string.IsNullOrWhiteSpace(egressId) ? null : $"egress:{egressId}");
        }

        private static string? ExtractTrackSidFromEgressPath(string? pathOrUrl)
        {
            if (string.IsNullOrWhiteSpace(pathOrUrl))
            {
                return null;
            }

            string path;
            if (Uri.TryCreate(pathOrUrl, UriKind.Absolute, out var uri))
            {
                path = uri.AbsolutePath;
            }
            else
            {
                path = pathOrUrl;
            }

            var fileName = path.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
            if (string.IsNullOrWhiteSpace(fileName))
            {
                return null;
            }

            const string trackPrefix = "track-";
            if (!fileName.StartsWith(trackPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var withoutPrefix = fileName[trackPrefix.Length..];
            var extensionIndex = withoutPrefix.LastIndexOf('.');
            return extensionIndex > 0 ? withoutPrefix[..extensionIndex] : withoutPrefix;
        }

        private static EgressWebhookDetails ExtractEgressDetails(string rawPayload)
        {
            if (string.IsNullOrWhiteSpace(rawPayload))
            {
                return new EgressWebhookDetails(null, null, null, null, null, null);
            }

            try
            {
                using var document = JsonDocument.Parse(rawPayload);
                if (!document.RootElement.TryGetProperty("egressInfo", out var egressInfo)
                    || egressInfo.ValueKind != JsonValueKind.Object)
                {
                    return new EgressWebhookDetails(null, null, null, null, null, null);
                }

                var egressId = GetString(egressInfo, "egressId") ?? GetString(egressInfo, "id");
                var trackSid = FirstNonEmpty(
                    GetString(egressInfo, "trackId"),
                    GetString(egressInfo, "audioTrackId"));
                var startedAtUtc = GetUnixTime(egressInfo, "startedAt") ?? GetUnixTime(egressInfo, "startedAtNs");
                var endedAtUtc = GetUnixTime(egressInfo, "endedAt") ?? GetUnixTime(egressInfo, "endedAtNs");
                var failureCode = FirstNonEmpty(
                    GetString(egressInfo, "errorCode"),
                    GetString(egressInfo, "twirpErrorCode"),
                    GetString(egressInfo, "serviceErrorCode"));
                var failureMessage = GetString(egressInfo, "error");

                return new EgressWebhookDetails(
                    egressId,
                    trackSid,
                    startedAtUtc,
                    endedAtUtc,
                    failureCode,
                    failureMessage);
            }
            catch (JsonException)
            {
                return new EgressWebhookDetails(null, null, null, null, null, null);
            }
        }

        private static string? GetString(JsonElement element, string propertyName)
        {
            return element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
                ? property.GetString()
                : null;
        }

        private static DateTime? GetUnixTime(JsonElement element, string propertyName)
        {
            if (!element.TryGetProperty(propertyName, out var property))
            {
                return null;
            }

            long value;
            if (property.ValueKind == JsonValueKind.Number)
            {
                if (!property.TryGetInt64(out value))
                {
                    return null;
                }
            }
            else if (property.ValueKind == JsonValueKind.String
                     && long.TryParse(property.GetString(), out var parsed))
            {
                value = parsed;
            }
            else
            {
                return null;
            }

            if (value <= 0)
            {
                return null;
            }

            // LiveKit protobuf JSON may use seconds (*At) or nanoseconds (*AtNs).
            return value > 10_000_000_000L
                ? DateTimeOffset.FromUnixTimeMilliseconds(value / 1_000_000).UtcDateTime
                : DateTimeOffset.FromUnixTimeSeconds(value).UtcDateTime;
        }

        private static string? FirstNonEmpty(params string?[] values)
            => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

        private static string? ExtractObjectKeyFromS3Url(string? url)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return null;
            }

            try
            {
                var uri = new Uri(url);
                var path = uri.AbsolutePath.TrimStart('/');
                var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);

                if (segments.Length < 2)
                {
                    return null;
                }

                var tracksSegmentIndex = Array.FindIndex(
                    segments,
                    segment => segment.Equals("tracks", StringComparison.OrdinalIgnoreCase));
                if (tracksSegmentIndex >= 0)
                {
                    return string.Join('/', segments.Skip(tracksSegmentIndex));
                }

                return string.Join('/', segments.Skip(1));
            }
            catch (UriFormatException)
            {
                return null;
            }
        }

        private sealed record EgressWebhookDetails(
            string? EgressId,
            string? TrackSid,
            DateTime? StartedAtUtc,
            DateTime? EndedAtUtc,
            string? FailureCode,
            string? FailureMessage);

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
            if (eventType == SessionEventType.TrackPublished
                && !string.IsNullOrWhiteSpace(webhookEvent.Track?.Sid))
            {
                return $"track-published:{meetingId}:{webhookEvent.Track.Sid}";
            }

            if (eventType == SessionEventType.EgressEnded)
            {
                var fileSignature = string.Join(
                    '|',
                    (webhookEvent.EgressInfo?.FileResults ?? [])
                    .Select(file => string.IsNullOrWhiteSpace(file.Filename) ? file.Location : file.Filename)
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Order(StringComparer.Ordinal));

                if (!string.IsNullOrWhiteSpace(fileSignature))
                {
                    var signature = $"{meetingId}:{webhookEvent.EgressInfo?.Status}:{fileSignature}";
                    return $"egress-ended:{meetingId}:{HashExternalEventSignature(signature)}";
                }
            }

            if (!string.IsNullOrWhiteSpace(webhookEvent.Id))
            {
                return webhookEvent.Id;
            }

            return $"{eventType}:{meetingId}:{webhookEvent.CreatedAt}";
        }

        private static string HashExternalEventSignature(string signature)
        {
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(signature));
            return Convert.ToHexString(hash)[..16].ToLowerInvariant();
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
                    foreach (var file in fileResults.EnumerateArray())
                    {
                        if (file.TryGetProperty("filename", out var filename))
                        {
                            var identity = ExtractParticipantIdentityFromEgressPath(filename.GetString());
                            if (!string.IsNullOrWhiteSpace(identity))
                            {
                                return identity;
                            }
                        }

                        if (file.TryGetProperty("location", out var location))
                        {
                            var identity = ExtractParticipantIdentityFromEgressPath(location.GetString());
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

        private static string? ExtractParticipantIdentityFromEgressPath(string? pathOrUrl)
        {
            if (string.IsNullOrWhiteSpace(pathOrUrl))
            {
                return null;
            }

            string path;
            if (Uri.TryCreate(pathOrUrl, UriKind.Absolute, out var uri))
            {
                path = uri.AbsolutePath;
            }
            else
            {
                path = pathOrUrl;
            }

            var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);

            var userSegment = segments.FirstOrDefault(segment =>
                segment.StartsWith("user:", StringComparison.OrdinalIgnoreCase));

            if (!string.IsNullOrWhiteSpace(userSegment))
            {
                return userSegment;
            }

            // Expected egress path format: tracks/{roomName}/{participantIdentity}/track-{trackId}.ogg.
            return segments.Length >= 3 ? segments[2] : null;
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
