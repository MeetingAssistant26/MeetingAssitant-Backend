using Hangfire;
using MeetingAssistant.Features.LiveSession.Infrastructure;
using MeetingAssistant.Features.LiveSession.Models;
using MeetingAssistant.Features.LiveSession.Models.Events;
using MeetingAssistant.Features.LiveSession.Models.PostProcessing;
using MeetingAssistant.Features.LiveSession.Services;
using MeetingAssistant.Features.LiveSession.Services.PostProcessing;
using MeetingAssistant.Infrastructure.Persistence.DbContext;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MeetingAssistant.Features.LiveSession.Jobs
{
    public class IngestParticipantAudioJob(
        ApplicationDbContext dbContext,
        IPublisher publisher,
        ILogger<IngestParticipantAudioJob> logger,
        IPostMeetingProcessingTracker? postMeetingProcessingTracker = null,
        IOptions<LiveKitOptions>? liveKitOptions = null,
        IParticipantAudioReadinessService? participantAudioReadinessService = null)
    {
        private const int DefaultParticipantAudioIngestCeilingMinutes = 360;

        private readonly ApplicationDbContext _dbContext = dbContext;
        private readonly IPublisher _publisher = publisher;
        private readonly ILogger<IngestParticipantAudioJob> _logger = logger;
        private readonly IPostMeetingProcessingTracker? _postMeetingProcessingTracker = postMeetingProcessingTracker;
        private readonly IParticipantAudioReadinessService _participantAudioReadinessService = participantAudioReadinessService
            ?? new ParticipantAudioReadinessService(dbContext);
        private readonly TimeSpan _participantAudioIngestCeiling = TimeSpan.FromMinutes(
            NormalizeParticipantAudioIngestCeilingMinutes(
                liveKitOptions?.Value.ParticipantAudioIngestCeilingMinutes));

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
                if (_postMeetingProcessingTracker is not null && track.Status == ParticipantAudioTrackStatus.Available)
                {
                    await _postMeetingProcessingTracker.CompleteStepAsync(
                        track.OrganizationId,
                        track.MeetingId,
                        PostMeetingProcessingStepType.AudioIngest,
                        message: "Participant audio track ingest was already complete.",
                        artifact: new PostMeetingArtifactLink("participant_audio_track", track.Id),
                        cancellationToken: cancellationToken);
                }

                _logger.LogInformation(
                    "Participant audio ingest skipped. TrackId={TrackId} StatusTransition={StatusTransition} StorageObjectKey={StorageObjectKey} SizeBytes={SizeBytes}",
                    trackId,
                    $"{track.Status}->{track.Status}",
                    track.StorageObjectKey,
                    track.SizeBytes);
                return;
            }

            var previousStatus = track.Status;
            if (_postMeetingProcessingTracker is not null)
            {
                await _postMeetingProcessingTracker.StartStepAsync(
                    track.OrganizationId,
                    track.MeetingId,
                    PostMeetingProcessingStepType.AudioIngest,
                    message: "Participant audio track ingest started.",
                    artifact: new PostMeetingArtifactLink("participant_audio_track", track.Id),
                    cancellationToken: cancellationToken);
            }

            var age = DateTime.UtcNow - track.CreatedAtUtc;
            if (age > _participantAudioIngestCeiling)
            {
                track.Status = ParticipantAudioTrackStatus.Failed;
                if (_postMeetingProcessingTracker is not null)
                {
                    await _postMeetingProcessingTracker.FailStepAsync(
                        track.OrganizationId,
                        track.MeetingId,
                        PostMeetingProcessingStepType.AudioIngest,
                        "ingest_timeout",
                        $"Participant audio ingest abandoned after {_participantAudioIngestCeiling.TotalMinutes:0}-minute ceiling.",
                        artifact: new PostMeetingArtifactLink("participant_audio_track", track.Id),
                        cancellationToken: cancellationToken);
                }
                var readyEvent = await PersistTerminalStatusAndTryCreateReadyEventAsync(track, cancellationToken);

                _logger.LogWarning(
                    "Participant audio ingest abandoned after configured ceiling. TrackId={TrackId} MeetingId={MeetingId} AgeMinutes={AgeMinutes} CeilingMinutes={CeilingMinutes} StatusTransition={StatusTransition}",
                    trackId,
                    track.MeetingId,
                    age.TotalMinutes,
                    _participantAudioIngestCeiling.TotalMinutes,
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
                if (_postMeetingProcessingTracker is not null)
                {
                    await _postMeetingProcessingTracker.FailStepAsync(
                        track.OrganizationId,
                        track.MeetingId,
                        PostMeetingProcessingStepType.AudioIngest,
                        "invalid_storage_location",
                        "Failed to extract object key from S3 location URL.",
                        artifact: new PostMeetingArtifactLink("participant_audio_track", track.Id),
                        cancellationToken: cancellationToken);
                }
                var readyEvent = await PersistTerminalStatusAndTryCreateReadyEventAsync(track, cancellationToken);
                if (readyEvent is not null)
                {
                    await _publisher.Publish(readyEvent, cancellationToken);
                }

                return;
            }

            try
            {
                track.StorageObjectKey = objectKey;
                track.SizeBytes = sizeBytes;
                track.Status = ParticipantAudioTrackStatus.Available;
                var readyEvent = await PersistTerminalStatusAndTryCreateReadyEventAsync(track, cancellationToken);
                if (_postMeetingProcessingTracker is not null)
                {
                    await _postMeetingProcessingTracker.CompleteStepAsync(
                        track.OrganizationId,
                        track.MeetingId,
                        PostMeetingProcessingStepType.AudioIngest,
                        message: "Participant audio track ingest completed.",
                        artifact: new PostMeetingArtifactLink("participant_audio_track", track.Id),
                        cancellationToken: cancellationToken);
                }

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
                if (_postMeetingProcessingTracker is not null)
                {
                    await _postMeetingProcessingTracker.FailStepAsync(
                        track.OrganizationId,
                        track.MeetingId,
                        PostMeetingProcessingStepType.AudioIngest,
                        "ingest_failed",
                        ex.Message,
                        artifact: new PostMeetingArtifactLink("participant_audio_track", track.Id),
                        cancellationToken: cancellationToken);
                }
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
                if (_postMeetingProcessingTracker is not null && fragment.Status == ParticipantAudioFragmentStatus.Available)
                {
                    await _postMeetingProcessingTracker.CompleteStepAsync(
                        fragment.OrganizationId,
                        fragment.MeetingId,
                        PostMeetingProcessingStepType.AudioIngest,
                        message: "Participant audio fragment ingest was already complete.",
                        artifact: new PostMeetingArtifactLink("participant_audio_fragment", fragment.Id),
                        cancellationToken: cancellationToken);
                }

                _logger.LogInformation(
                    "Participant audio fragment ingest skipped. FragmentId={FragmentId} StatusTransition={StatusTransition} StorageObjectKey={StorageObjectKey} SizeBytes={SizeBytes}",
                    fragmentId,
                    $"{fragment.Status}->{fragment.Status}",
                    fragment.StorageObjectKey,
                    fragment.SizeBytes);
                return;
            }

            var previousStatus = fragment.Status;
            if (_postMeetingProcessingTracker is not null)
            {
                await _postMeetingProcessingTracker.StartStepAsync(
                    fragment.OrganizationId,
                    fragment.MeetingId,
                    PostMeetingProcessingStepType.AudioIngest,
                    message: "Participant audio fragment ingest started.",
                    artifact: new PostMeetingArtifactLink("participant_audio_fragment", fragment.Id),
                    cancellationToken: cancellationToken);
            }

            var age = DateTime.UtcNow - fragment.CreatedAtUtc;
            if (age > _participantAudioIngestCeiling)
            {
                fragment.Status = ParticipantAudioFragmentStatus.Failed;
                fragment.FailedAtUtc = DateTime.UtcNow;
                fragment.FailureCode = "ingest_timeout";
                fragment.FailureMessage = $"Participant audio fragment ingest abandoned after {_participantAudioIngestCeiling.TotalMinutes:0}-minute ceiling.";
                if (_postMeetingProcessingTracker is not null)
                {
                    await _postMeetingProcessingTracker.FailStepAsync(
                        fragment.OrganizationId,
                        fragment.MeetingId,
                        PostMeetingProcessingStepType.AudioIngest,
                        fragment.FailureCode,
                        fragment.FailureMessage,
                        artifact: new PostMeetingArtifactLink("participant_audio_fragment", fragment.Id),
                        cancellationToken: cancellationToken);
                }

                var readyEvent = await PersistFragmentTerminalStatusAndTryCreateReadyEventAsync(fragment, cancellationToken);

                _logger.LogWarning(
                    "Participant audio fragment ingest abandoned after configured ceiling. FragmentId={FragmentId} MeetingId={MeetingId} AgeMinutes={AgeMinutes} CeilingMinutes={CeilingMinutes} StatusTransition={StatusTransition}",
                    fragmentId,
                    fragment.MeetingId,
                    age.TotalMinutes,
                    _participantAudioIngestCeiling.TotalMinutes,
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
                if (_postMeetingProcessingTracker is not null)
                {
                    await _postMeetingProcessingTracker.FailStepAsync(
                        fragment.OrganizationId,
                        fragment.MeetingId,
                        PostMeetingProcessingStepType.AudioIngest,
                        fragment.FailureCode,
                        fragment.FailureMessage,
                        artifact: new PostMeetingArtifactLink("participant_audio_fragment", fragment.Id),
                        cancellationToken: cancellationToken);
                }
                var readyEvent = await PersistFragmentTerminalStatusAndTryCreateReadyEventAsync(fragment, cancellationToken);
                if (readyEvent is not null)
                {
                    await _publisher.Publish(readyEvent, cancellationToken);
                }

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
                if (_postMeetingProcessingTracker is not null)
                {
                    await _postMeetingProcessingTracker.CompleteStepAsync(
                        fragment.OrganizationId,
                        fragment.MeetingId,
                        PostMeetingProcessingStepType.AudioIngest,
                        message: "Participant audio fragment ingest completed.",
                        artifact: new PostMeetingArtifactLink("participant_audio_fragment", fragment.Id),
                        cancellationToken: cancellationToken);
                }

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
                if (_postMeetingProcessingTracker is not null)
                {
                    await _postMeetingProcessingTracker.FailStepAsync(
                        fragment.OrganizationId,
                        fragment.MeetingId,
                        PostMeetingProcessingStepType.AudioIngest,
                        fragment.FailureCode,
                        fragment.FailureMessage,
                        artifact: new PostMeetingArtifactLink("participant_audio_fragment", fragment.Id),
                        cancellationToken: cancellationToken);
                }
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

        private static int NormalizeParticipantAudioIngestCeilingMinutes(int? configuredMinutes)
        {
            return configuredMinutes is > 0
                ? configuredMinutes.Value
                : DefaultParticipantAudioIngestCeilingMinutes;
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

        private async Task<ParticipantAudioReadyEvent?> PersistTerminalStatusAndTryCreateReadyEventAsync(
            ParticipantAudioTrack track,
            CancellationToken cancellationToken)
        {
            await _dbContext.SaveChangesAsync(cancellationToken);

            return await _participantAudioReadinessService.TryCreateReadyEventAsync(
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

            return await _participantAudioReadinessService.TryCreateReadyEventAsync(
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

    }
}
