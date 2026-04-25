using MeetingAssistant.Features.LiveSession.Models;
using MeetingAssistant.Features.LiveSession.Models.Events;
using MeetingAssistant.Infrastructure.Persistence.DbContext;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace MeetingAssistant.Features.LiveSession.Jobs
{
    public class IngestParticipantAudioJob(
        ApplicationDbContext dbContext,
        IPublisher publisher,
        ILogger<IngestParticipantAudioJob> logger)
    {
        private const string UniqueViolationSqlState = "23505";

        private readonly ApplicationDbContext _dbContext = dbContext;
        private readonly IPublisher _publisher = publisher;
        private readonly ILogger<IngestParticipantAudioJob> _logger = logger;

        public async Task RunAsync(
            Guid trackId,
            string s3LocationUrl,
            long? sizeBytes,
            CancellationToken cancellationToken = default)
        {
            var track = await _dbContext.ParticipantAudioTracks
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(x => x.Id == trackId, cancellationToken);

            if (track == null)
            {
                _logger.LogWarning(
                    "Participant audio ingest skipped. TrackId={TrackId} Reason={Reason}",
                    trackId,
                    "track_row_missing");
                return;
            }

            if (track.Status is ParticipantAudioTrackStatus.Available or ParticipantAudioTrackStatus.Failed)
            {
                _logger.LogInformation(
                    "Participant audio ingest skipped. TrackId={TrackId} StatusTransition={StatusTransition} StorageObjectKey={StorageObjectKey} SizeBytes={SizeBytes}",
                    trackId,
                    $"{track.Status}->{track.Status}",
                    track.StorageObjectKey,
                    track.SizeBytes);
                return;
            }

            var previousStatus = track.Status;
            var objectKey = ExtractObjectKeyFromS3Url(s3LocationUrl);

            if (string.IsNullOrWhiteSpace(objectKey))
            {
                _logger.LogError(
                    "Failed to extract object key from S3 location URL. TrackId={TrackId} Url={S3LocationUrl}",
                    trackId,
                    s3LocationUrl);
                track.Status = ParticipantAudioTrackStatus.Failed;
                await PersistTerminalStatusAsync(track, cancellationToken);
                return;
            }

            try
            {
                track.StorageObjectKey = objectKey;
                track.SizeBytes = sizeBytes;
                track.Status = ParticipantAudioTrackStatus.Available;
                var readyEvent = await PersistTerminalStatusAndTryCreateReadyEventAsync(track, cancellationToken);

                _logger.LogInformation(
                    "Participant audio ingest completed. TrackId={TrackId} MeetingId={MeetingId} StatusTransition={StatusTransition} StorageObjectKey={StorageObjectKey} SizeBytes={SizeBytes}",
                    trackId,
                    track.MeetingId,
                    $"{previousStatus}->{track.Status}",
                    objectKey,
                    sizeBytes);

                if (readyEvent is not null)
                {
                    await _publisher.Publish(readyEvent, cancellationToken);
                }
            }
            catch (Exception ex)
            {
                track.Status = ParticipantAudioTrackStatus.Failed;
                var readyEvent = await PersistTerminalStatusAndTryCreateReadyEventAsync(track, cancellationToken);

                _logger.LogError(
                    ex,
                    "Participant audio ingest failed. TrackId={TrackId} MeetingId={MeetingId} StatusTransition={StatusTransition} StorageObjectKey={StorageObjectKey} SizeBytes={SizeBytes}",
                    trackId,
                    track.MeetingId,
                    $"{previousStatus}->{track.Status}",
                    objectKey,
                    sizeBytes);

                if (readyEvent is not null)
                {
                    await _publisher.Publish(readyEvent, cancellationToken);
                }
            }
        }

        private static string? ExtractObjectKeyFromS3Url(string? url)
        {
            if (string.IsNullOrWhiteSpace(url))
                return null;

            try
            {
                var uri = new Uri(url);
                var path = uri.AbsolutePath.TrimStart('/');
                var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);

                if (segments.Length < 2)
                    return null;

                // Path-style: /bucket/key/parts → skip bucket segment
                // Virtual-hosted: /key/parts → first segment is already the key start
                // Heuristic: if the first segment looks like a bucket (no dots), skip it
                // In practice for MinIO with ForcePathStyle, it's always path-style.
                var key = string.Join('/', segments.Skip(1));
                return key;
            }
            catch (UriFormatException)
            {
                return null;
            }
        }

        private async Task PersistTerminalStatusAsync(
            ParticipantAudioTrack track,
            CancellationToken cancellationToken)
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
        }

        private async Task<ParticipantAudioReadyEvent?> PersistTerminalStatusAndTryCreateReadyEventAsync(
            ParticipantAudioTrack track,
            CancellationToken cancellationToken)
        {
            await _dbContext.SaveChangesAsync(cancellationToken);

            return await TryCreateParticipantAudioReadyEventAsync(
                track.MeetingId,
                track.OrganizationId,
                cancellationToken);
        }

        private async Task<ParticipantAudioReadyEvent?> TryCreateParticipantAudioReadyEventAsync(
            Guid meetingId,
            Guid organizationId,
            CancellationToken cancellationToken)
        {
            var remainingTracks = await _dbContext.ParticipantAudioTracks
                .IgnoreQueryFilters()
                .CountAsync(
                    x => x.MeetingId == meetingId
                         && x.Status != ParticipantAudioTrackStatus.Available
                         && x.Status != ParticipantAudioTrackStatus.Failed,
                    cancellationToken);

            if (remainingTracks > 0)
            {
                return null;
            }

            var occurredAtUtc = DateTime.UtcNow;
            _dbContext.SessionEvents.Add(new SessionEvent
            {
                MeetingId = meetingId,
                OrganizationId = organizationId,
                EventType = SessionEventType.ParticipantAudioReady,
                ExternalEventId = $"participant-audio-ready:{meetingId}",
                PayloadJson = "{}",
                OccurredAtUtc = occurredAtUtc,
                ProcessedAtUtc = occurredAtUtc
            });

            try
            {
                await _dbContext.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException ex) when (IsUniqueViolation(ex))
            {
                _dbContext.ChangeTracker.Clear();
                return null;
            }

            return new ParticipantAudioReadyEvent(meetingId, organizationId, occurredAtUtc);
        }

        private static bool IsUniqueViolation(DbUpdateException exception)
        {
            return exception.InnerException is PostgresException postgresException
                && postgresException.SqlState == UniqueViolationSqlState;
        }
    }
}
