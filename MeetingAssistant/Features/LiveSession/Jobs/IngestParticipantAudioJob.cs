using Hangfire;
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

        [AutomaticRetry(Attempts = 3)]
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

            var age = DateTime.UtcNow - track.CreatedAtUtc;
            if (age > TimeSpan.FromMinutes(8))
            {
                track.Status = ParticipantAudioTrackStatus.Failed;
                var readyEvent = await PersistTerminalStatusAndTryCreateReadyEventAsync(track, cancellationToken);

                _logger.LogWarning(
                    "Participant audio ingest abandoned after 8-minute ceiling. TrackId={TrackId} MeetingId={MeetingId} AgeMinutes={AgeMinutes} StatusTransition={StatusTransition}",
                    trackId,
                    track.MeetingId,
                    age.TotalMinutes,
                    $"{previousStatus}->{track.Status}");

                if (readyEvent is not null)
                {
                    await _publisher.Publish(readyEvent, cancellationToken);
                }

                return;
            }
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

        [AutomaticRetry(Attempts = 3)]
        public async Task RunFragmentAsync(
            Guid fragmentId,
            string s3LocationUrl,
            long? sizeBytes,
            CancellationToken cancellationToken = default)
        {
            var fragment = await _dbContext.ParticipantAudioFragments
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(x => x.Id == fragmentId, cancellationToken);

            if (fragment == null)
            {
                _logger.LogWarning(
                    "Participant audio fragment ingest skipped. FragmentId={FragmentId} Reason={Reason}",
                    fragmentId,
                    "fragment_row_missing");
                return;
            }

            if (fragment.Status is ParticipantAudioFragmentStatus.Available or ParticipantAudioFragmentStatus.Failed)
            {
                _logger.LogInformation(
                    "Participant audio fragment ingest skipped. FragmentId={FragmentId} StatusTransition={StatusTransition} StorageObjectKey={StorageObjectKey} SizeBytes={SizeBytes}",
                    fragmentId,
                    $"{fragment.Status}->{fragment.Status}",
                    fragment.StorageObjectKey,
                    fragment.SizeBytes);
                return;
            }

            var previousStatus = fragment.Status;

            var age = DateTime.UtcNow - fragment.CreatedAtUtc;
            if (age > TimeSpan.FromMinutes(8))
            {
                fragment.Status = ParticipantAudioFragmentStatus.Failed;
                fragment.FailedAtUtc = DateTime.UtcNow;
                fragment.FailureCode = "ingest_timeout";
                fragment.FailureMessage = "Participant audio fragment ingest abandoned after 8-minute ceiling.";

                var readyEvent = await PersistFragmentTerminalStatusAndTryCreateReadyEventAsync(fragment, cancellationToken);

                _logger.LogWarning(
                    "Participant audio fragment ingest abandoned after 8-minute ceiling. FragmentId={FragmentId} MeetingId={MeetingId} AgeMinutes={AgeMinutes} StatusTransition={StatusTransition}",
                    fragmentId,
                    fragment.MeetingId,
                    age.TotalMinutes,
                    $"{previousStatus}->{fragment.Status}");

                if (readyEvent is not null)
                {
                    await _publisher.Publish(readyEvent, cancellationToken);
                }

                return;
            }

            var objectKey = ExtractObjectKeyFromS3Url(s3LocationUrl);

            if (string.IsNullOrWhiteSpace(objectKey))
            {
                _logger.LogError(
                    "Failed to extract object key from S3 location URL. FragmentId={FragmentId} Url={S3LocationUrl}",
                    fragmentId,
                    s3LocationUrl);

                fragment.Status = ParticipantAudioFragmentStatus.Failed;
                fragment.FailedAtUtc = DateTime.UtcNow;
                fragment.FailureCode = "invalid_storage_location";
                fragment.FailureMessage = "Failed to extract object key from S3 location URL.";
                await PersistFragmentTerminalStatusAndTryCreateReadyEventAsync(fragment, cancellationToken);
                return;
            }

            try
            {
                fragment.StorageLocation = s3LocationUrl;
                fragment.StorageObjectKey = objectKey;
                fragment.SizeBytes = sizeBytes;
                fragment.Status = ParticipantAudioFragmentStatus.Available;
                fragment.StorageAvailableAtUtc = DateTime.UtcNow;
                fragment.FailedAtUtc = null;
                fragment.FailureCode = null;
                fragment.FailureMessage = null;

                var readyEvent = await PersistFragmentTerminalStatusAndTryCreateReadyEventAsync(fragment, cancellationToken);

                _logger.LogInformation(
                    "Participant audio fragment ingest completed. FragmentId={FragmentId} MeetingId={MeetingId} TrackSid={TrackSid} StatusTransition={StatusTransition} StorageObjectKey={StorageObjectKey} SizeBytes={SizeBytes}",
                    fragmentId,
                    fragment.MeetingId,
                    fragment.TrackSid,
                    $"{previousStatus}->{fragment.Status}",
                    objectKey,
                    sizeBytes);

                if (readyEvent is not null)
                {
                    await _publisher.Publish(readyEvent, cancellationToken);
                }
            }
            catch (Exception ex)
            {
                fragment.Status = ParticipantAudioFragmentStatus.Failed;
                fragment.FailedAtUtc = DateTime.UtcNow;
                fragment.FailureCode = "ingest_failed";
                fragment.FailureMessage = ex.Message;
                var readyEvent = await PersistFragmentTerminalStatusAndTryCreateReadyEventAsync(fragment, cancellationToken);

                _logger.LogError(
                    ex,
                    "Participant audio fragment ingest failed. FragmentId={FragmentId} MeetingId={MeetingId} TrackSid={TrackSid} StatusTransition={StatusTransition} StorageObjectKey={StorageObjectKey} SizeBytes={SizeBytes}",
                    fragmentId,
                    fragment.MeetingId,
                    fragment.TrackSid,
                    $"{previousStatus}->{fragment.Status}",
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

                var tracksSegmentIndex = Array.FindIndex(
                    segments,
                    segment => segment.Equals("tracks", StringComparison.OrdinalIgnoreCase));
                if (tracksSegmentIndex >= 0)
                    return string.Join('/', segments.Skip(tracksSegmentIndex));

                // Path-style: /bucket/key/parts → skip bucket segment
                // Fallback for legacy paths without the expected tracks/ prefix: skip bucket segment.
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

        private async Task<ParticipantAudioReadyEvent?> PersistFragmentTerminalStatusAndTryCreateReadyEventAsync(
            ParticipantAudioFragment fragment,
            CancellationToken cancellationToken)
        {
            if (fragment.ParticipantAudioTrackId is { } trackId)
            {
                await RefreshParticipantAudioTrackAggregateAsync(trackId, cancellationToken);
            }

            await _dbContext.SaveChangesAsync(cancellationToken);

            return await TryCreateParticipantAudioReadyEventAsync(
                fragment.MeetingId,
                fragment.OrganizationId,
                cancellationToken);
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
