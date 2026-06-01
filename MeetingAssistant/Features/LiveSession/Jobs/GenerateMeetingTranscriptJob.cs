using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using MediatR;
using MeetingAssistant.Features.LiveSession.Models;
using MeetingAssistant.Features.LiveSession.Models.Events;
using MeetingAssistant.Features.LiveSession.Services;
using MeetingAssistant.Infrastructure.Persistence.DbContext;
using Microsoft.EntityFrameworkCore;

namespace MeetingAssistant.Features.LiveSession.Jobs
{
    public class GenerateMeetingTranscriptJob(
        ApplicationDbContext dbContext,
        ISttService sttService,
        IPublisher publisher,
        ILogger<GenerateMeetingTranscriptJob> logger)
    {
        private readonly ApplicationDbContext _dbContext = dbContext;
        private readonly ISttService _sttService = sttService;
        private readonly IPublisher _publisher = publisher;
        private readonly ILogger<GenerateMeetingTranscriptJob> _logger = logger;

        private static readonly Regex TrackSidFromObjectKeyRegex = new(
            @"(?:^|/)track-(?<sid>[^/.\\]+)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public async Task RunAsync(
            Guid meetingId,
            Guid organizationId,
            CancellationToken cancellationToken = default)
        {
            var tracks = await _dbContext.ParticipantAudioTracks
                .IgnoreQueryFilters()
                .Where(x => x.MeetingId == meetingId
                            && x.OrganizationId == organizationId
                            && x.Status == ParticipantAudioTrackStatus.Available
                            && x.StorageObjectKey != null)
                .Select(x => new
                {
                    x.Id,
                    x.ParticipantUserId,
                    x.StorageObjectKey
                })
                .ToListAsync(cancellationToken);

            if (tracks.Count == 0)
            {
                _logger.LogInformation(
                    "Transcript generation skipped because no available participant tracks were found. MeetingId={MeetingId}",
                    meetingId);
                return;
            }

            var meeting = await _dbContext.Meetings
                .IgnoreQueryFilters()
                .Where(x => x.Id == meetingId && x.OrganizationId == organizationId)
                .Select(x => new { x.RoomActivatedAtUtc })
                .FirstOrDefaultAsync(cancellationToken);

            var timingEvents = await _dbContext.SessionEvents
                .IgnoreQueryFilters()
                .Where(x => x.MeetingId == meetingId
                            && x.OrganizationId == organizationId
                            && (x.EventType == SessionEventType.RoomStarted
                                || x.EventType == SessionEventType.TrackPublished
                                || x.EventType == SessionEventType.ParticipantJoined))
                .Select(x => new TranscriptTimingEvent(
                    x.EventType,
                    x.ParticipantUserId,
                    x.ExternalEventId,
                    x.PayloadJson,
                    x.OccurredAtUtc))
                .ToListAsync(cancellationToken);

            var roomActivatedAtUtc = meeting?.RoomActivatedAtUtc
                ?? timingEvents
                    .Where(x => x.EventType == SessionEventType.RoomStarted)
                    .OrderBy(x => x.OccurredAtUtc)
                    .Select(x => (DateTime?)x.OccurredAtUtc)
                    .FirstOrDefault();

            var tracksWithTiming = tracks
                .Select(track => new
                {
                    track.Id,
                    track.ParticipantUserId,
                    track.StorageObjectKey,
                    Timing = ResolveTrackTiming(
                        track.ParticipantUserId,
                        track.StorageObjectKey!,
                        roomActivatedAtUtc,
                        timingEvents)
                })
                .ToList();

            var allSegments = new ConcurrentBag<NormalizedTranscriptSegment>();
            var sttModels = new ConcurrentBag<string>();

            await Parallel.ForEachAsync(
                tracksWithTiming,
                new ParallelOptions
                {
                    CancellationToken = cancellationToken,
                    MaxDegreeOfParallelism = 4
                },
                async (track, ct) =>
                {
                    try
                    {
                        var result = await _sttService.TranscribeTrackAsync(
                            track.ParticipantUserId,
                            track.StorageObjectKey!,
                            ct);

                        sttModels.Add(result.Model);
                        foreach (var segment in result.Segments)
                        {
                            allSegments.Add(NormalizeSegment(
                                segment,
                                track.Id,
                                roomActivatedAtUtc,
                                track.Timing));
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(
                            ex,
                            "STT transcription failed for participant track. MeetingId={MeetingId} ParticipantUserId={ParticipantUserId}",
                            meetingId,
                            track.ParticipantUserId);
                    }
                });

            if (allSegments.IsEmpty)
            {
                _logger.LogWarning(
                    "Transcript generation skipped because all participant transcriptions failed. MeetingId={MeetingId}",
                    meetingId);
                return;
            }

            var orderedSegments = allSegments
                .OrderBy(x => x.RoomRelativeStartMs)
                .ThenBy(x => x.RoomRelativeEndMs)
                .ToList();

            var participantIds = orderedSegments
                .Select(x => x.ParticipantUserId)
                .Distinct()
                .ToList();

            var participantProfiles = await _dbContext.MeetingParticipants
                .IgnoreQueryFilters()
                .Where(x => x.MeetingId == meetingId && x.OrganizationId == organizationId && participantIds.Contains(x.UserId))
                .Select(x => new
                {
                    x.UserId,
                    x.User.DisplayName,
                    x.User.UserName
                })
                .ToListAsync(cancellationToken);

            var displayNames = participantProfiles.ToDictionary(
                x => x.UserId,
                x => string.IsNullOrWhiteSpace(x.DisplayName) ? x.UserName ?? x.UserId.ToString() : x.DisplayName);

            var fullText = string.Join(
                Environment.NewLine,
                orderedSegments.Select(segment =>
                {
                    var displayName = displayNames.TryGetValue(segment.ParticipantUserId, out var resolved)
                        ? resolved
                        : segment.ParticipantUserId.ToString();

                    return $"[{FormatTimestamp(segment.StartMs)} {displayName}] {segment.Text}";
                }));

            var segmentsJson = JsonSerializer.Serialize(
                orderedSegments.Select(segment => new PersistedTranscriptSegment(
                    2,
                    segment.ParticipantUserId,
                    segment.ParticipantAudioTrackId,
                    segment.TrackRelativeStartMs,
                    segment.TrackRelativeEndMs,
                    segment.RoomRelativeStartMs,
                    segment.RoomRelativeEndMs,
                    segment.AbsoluteStartUtc,
                    segment.AbsoluteEndUtc,
                    // Backward-compatible aliases used by existing read DTOs. For v2 rows these are room-relative.
                    segment.RoomRelativeStartMs,
                    segment.RoomRelativeEndMs,
                    segment.Text,
                    segment.AvgLogProb,
                    segment.TimestampOffsetSource)));

            var transcript = await _dbContext.MeetingTranscripts
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(x => x.MeetingId == meetingId && x.OrganizationId == organizationId, cancellationToken);

            if (transcript == null)
            {
                transcript = new MeetingTranscript
                {
                    MeetingId = meetingId,
                    OrganizationId = organizationId
                };

                _dbContext.MeetingTranscripts.Add(transcript);
            }

            transcript.FullText = fullText;
            transcript.SegmentsJson = segmentsJson;
            transcript.SttModel = sttModels.FirstOrDefault() ?? string.Empty;
            transcript.GeneratedAtUtc = DateTime.UtcNow;

            await _dbContext.SaveChangesAsync(cancellationToken);

            await _publisher.Publish(
                new MeetingTranscriptReadyEvent(meetingId, organizationId, DateTime.UtcNow),
                cancellationToken);
        }

        private static string FormatTimestamp(long milliseconds)
        {
            var time = TimeSpan.FromMilliseconds(milliseconds);
            return $"{(int)time.TotalHours:00}:{time.Minutes:00}:{time.Seconds:00}";
        }

        private static NormalizedTranscriptSegment NormalizeSegment(
            TranscriptSegment segment,
            Guid participantAudioTrackId,
            DateTime? roomActivatedAtUtc,
            TrackTiming timing)
        {
            var roomRelativeStartMs = timing.RoomRelativeStartOffsetMs + segment.StartMs;
            var roomRelativeEndMs = timing.RoomRelativeStartOffsetMs + segment.EndMs;

            var absoluteStartUtc = timing.AnchorUtc.HasValue && roomActivatedAtUtc.HasValue
                ? roomActivatedAtUtc.Value.AddMilliseconds(roomRelativeStartMs)
                : (DateTime?)null;
            var absoluteEndUtc = timing.AnchorUtc.HasValue && roomActivatedAtUtc.HasValue
                ? roomActivatedAtUtc.Value.AddMilliseconds(roomRelativeEndMs)
                : (DateTime?)null;

            return new NormalizedTranscriptSegment(
                segment.ParticipantUserId,
                participantAudioTrackId,
                segment.StartMs,
                segment.EndMs,
                roomRelativeStartMs,
                roomRelativeEndMs,
                absoluteStartUtc,
                absoluteEndUtc,
                segment.Text,
                segment.AvgLogProb,
                timing.Source);
        }

        private static TrackTiming ResolveTrackTiming(
            Guid participantUserId,
            string storageObjectKey,
            DateTime? roomActivatedAtUtc,
            IReadOnlyList<TranscriptTimingEvent> timingEvents)
        {
            if (!roomActivatedAtUtc.HasValue)
            {
                return new TrackTiming(0, null, TimestampOffsetSources.LegacyUnknown);
            }

            var participantEvents = timingEvents
                .Where(x => x.ParticipantUserId == participantUserId)
                .ToList();

            var trackSid = TryExtractTrackSid(storageObjectKey);
            TranscriptTimingEvent? trackPublished = null;

            if (!string.IsNullOrWhiteSpace(trackSid))
            {
                trackPublished = participantEvents
                    .Where(x => x.EventType == SessionEventType.TrackPublished)
                    .Where(x => EventMatchesTrackSid(x, trackSid))
                    .OrderByDescending(x => x.OccurredAtUtc)
                    .FirstOrDefault();
            }

            trackPublished ??= participantEvents
                .Where(x => x.EventType == SessionEventType.TrackPublished)
                .OrderByDescending(x => x.OccurredAtUtc)
                .FirstOrDefault();

            if (trackPublished is not null)
            {
                return CreateTiming(roomActivatedAtUtc.Value, trackPublished.OccurredAtUtc, TimestampOffsetSources.TrackPublished);
            }

            var participantJoined = participantEvents
                .Where(x => x.EventType == SessionEventType.ParticipantJoined)
                .OrderByDescending(x => x.OccurredAtUtc)
                .FirstOrDefault();

            return participantJoined is not null
                ? CreateTiming(roomActivatedAtUtc.Value, participantJoined.OccurredAtUtc, TimestampOffsetSources.ParticipantJoined)
                : new TrackTiming(0, null, TimestampOffsetSources.LegacyUnknown);
        }

        private static TrackTiming CreateTiming(DateTime roomActivatedAtUtc, DateTime anchorUtc, string source)
        {
            // Webhooks may arrive with coarse second precision or slightly out of order. Do not let
            // that produce negative room-relative transcript offsets; legacy/ambiguous cases still
            // retain track-relative behavior via the LegacyUnknown source.
            var offsetMs = Math.Max(
                0,
                (long)Math.Round((anchorUtc - roomActivatedAtUtc).TotalMilliseconds));

            return new TrackTiming(offsetMs, anchorUtc, source);
        }

        private static string? TryExtractTrackSid(string storageObjectKey)
        {
            var match = TrackSidFromObjectKeyRegex.Match(storageObjectKey);
            return match.Success ? match.Groups["sid"].Value : null;
        }

        private static bool EventMatchesTrackSid(TranscriptTimingEvent timingEvent, string trackSid)
        {
            if (timingEvent.ExternalEventId.EndsWith($":{trackSid}", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return !string.IsNullOrWhiteSpace(timingEvent.PayloadJson)
                   && timingEvent.PayloadJson.Contains(trackSid, StringComparison.OrdinalIgnoreCase);
        }

        private static class TimestampOffsetSources
        {
            public const string TrackPublished = "track_published";
            public const string ParticipantJoined = "participant_joined";
            public const string LegacyUnknown = "legacy_unknown";
        }

        private sealed record TrackTiming(
            long RoomRelativeStartOffsetMs,
            DateTime? AnchorUtc,
            string Source);

        private sealed record TranscriptTimingEvent(
            SessionEventType EventType,
            Guid? ParticipantUserId,
            string ExternalEventId,
            string PayloadJson,
            DateTime OccurredAtUtc);

        private sealed record NormalizedTranscriptSegment(
            Guid ParticipantUserId,
            Guid ParticipantAudioTrackId,
            long TrackRelativeStartMs,
            long TrackRelativeEndMs,
            long RoomRelativeStartMs,
            long RoomRelativeEndMs,
            DateTime? AbsoluteStartUtc,
            DateTime? AbsoluteEndUtc,
            string Text,
            double? AvgLogProb,
            string TimestampOffsetSource)
        {
            public long StartMs => RoomRelativeStartMs;
            public long EndMs => RoomRelativeEndMs;
        }

        private sealed record PersistedTranscriptSegment(
            int Version,
            Guid ParticipantUserId,
            Guid ParticipantAudioTrackId,
            long TrackRelativeStartMs,
            long TrackRelativeEndMs,
            long RoomRelativeStartMs,
            long RoomRelativeEndMs,
            DateTime? AbsoluteStartUtc,
            DateTime? AbsoluteEndUtc,
            long StartMs,
            long EndMs,
            string Text,
            double? AvgLogProb,
            string TimestampOffsetSource);
    }
}
