using System.Collections.Concurrent;
using System.Text.Json;
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

        public async Task RunAsync(
            Guid meetingId,
            Guid organizationId,
            CancellationToken cancellationToken = default)
        {
            var tracks = await _dbContext.ParticipantAudioTracks
                .Where(x => x.MeetingId == meetingId
                            && x.Status == ParticipantAudioTrackStatus.Available
                            && x.StorageObjectKey != null)
                .Select(x => new
                {
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

            var allSegments = new ConcurrentBag<TranscriptSegment>();
            var sttModels = new ConcurrentBag<string>();

            await Parallel.ForEachAsync(
                tracks,
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
                            allSegments.Add(segment);
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
                .OrderBy(x => x.StartMs)
                .ThenBy(x => x.EndMs)
                .ToList();

            var participantIds = orderedSegments
                .Select(x => x.ParticipantUserId)
                .Distinct()
                .ToList();

            var participantProfiles = await _dbContext.MeetingParticipants
                .Where(x => x.MeetingId == meetingId && participantIds.Contains(x.UserId))
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
                    segment.ParticipantUserId,
                    segment.StartMs,
                    segment.EndMs,
                    segment.Text,
                    segment.AvgLogProb)));

            var transcript = await _dbContext.MeetingTranscripts
                .FirstOrDefaultAsync(x => x.MeetingId == meetingId, cancellationToken);

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

        private sealed record PersistedTranscriptSegment(
            Guid ParticipantUserId,
            long StartMs,
            long EndMs,
            string Text,
            double? AvgLogProb);
    }
}
