using Hangfire;
using MediatR;
using MeetingAssistant.Features.LiveSession.Models;
using MeetingAssistant.Features.LiveSession.Models.Events;
using MeetingAssistant.Features.LiveSession.Models.PostProcessing;
using MeetingAssistant.Features.LiveSession.Services;
using MeetingAssistant.Features.LiveSession.Services.PostProcessing;
using MeetingAssistant.Infrastructure.Persistence.DbContext;
using Microsoft.EntityFrameworkCore;

namespace MeetingAssistant.Features.LiveSession.Jobs
{
    public sealed class PersistParticipantAudioFragmentJob(
        ApplicationDbContext dbContext,
        IStorageService storageService,
        IPublisher publisher,
        ILogger<PersistParticipantAudioFragmentJob> logger,
        IPostMeetingProcessingTracker? postMeetingProcessingTracker = null,
        IParticipantAudioReadinessService? participantAudioReadinessService = null)
    {
        private static readonly TimeSpan UploadLeaseDuration = TimeSpan.FromMinutes(5);

        private readonly ApplicationDbContext _dbContext = dbContext;
        private readonly IStorageService _storageService = storageService;
        private readonly IPublisher _publisher = publisher;
        private readonly ILogger<PersistParticipantAudioFragmentJob> _logger = logger;
        private readonly IPostMeetingProcessingTracker? _postMeetingProcessingTracker = postMeetingProcessingTracker;
        private readonly IParticipantAudioReadinessService _participantAudioReadinessService = participantAudioReadinessService
            ?? new ParticipantAudioReadinessService(dbContext);

        [AutomaticRetry(Attempts = 5)]
        public async Task RunAsync(Guid fragmentId, CancellationToken cancellationToken = default)
        {
            var now = DateTime.UtcNow;
            var leaseUntil = now.Add(UploadLeaseDuration);

            var claimed = await _dbContext.ParticipantAudioFragments
                .IgnoreQueryFilters()
                .Where(x => x.Id == fragmentId)
                .Where(x => x.Status == ParticipantAudioFragmentStatus.Pending)
                .Where(x => x.BackupStoragePath != null)
                .Where(x => x.StorageUploadLeaseExpiresAtUtc == null || x.StorageUploadLeaseExpiresAtUtc <= now)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(x => x.StorageUploadLeaseExpiresAtUtc, leaseUntil)
                        .SetProperty(x => x.LastStorageUploadAttemptAtUtc, now)
                        .SetProperty(x => x.StorageUploadAttemptCount, x => x.StorageUploadAttemptCount + 1)
                        .SetProperty(x => x.UpdatedAtUtc, now),
                    cancellationToken);

            if (claimed == 0)
            {
                _logger.LogInformation(
                    "Participant audio backup persist skipped because fragment is already claimed or terminal. FragmentId={FragmentId}",
                    fragmentId);
                return;
            }

            var fragment = await _dbContext.ParticipantAudioFragments
                .IgnoreQueryFilters()
                .FirstAsync(x => x.Id == fragmentId, cancellationToken);

            var backupPath = fragment.BackupStoragePath;
            var objectKey = FirstNonEmpty(
                fragment.StorageObjectKey,
                ExtractObjectKeyFromPath(backupPath),
                BuildFallbackObjectKey(fragment));

            try
            {
                var upload = await _storageService.UploadFileAsync(backupPath!, objectKey!, cancellationToken);

                fragment.StorageObjectKey = objectKey;
                fragment.StorageLocation = upload.StorageLocation;
                fragment.SizeBytes = upload.SizeBytes ?? fragment.SizeBytes;
                fragment.Status = ParticipantAudioFragmentStatus.Available;
                fragment.StorageAvailableAtUtc = DateTime.UtcNow;
                fragment.StorageUploadLeaseExpiresAtUtc = null;
                fragment.FailedAtUtc = null;
                fragment.FailureCode = null;
                fragment.FailureMessage = null;

                if (fragment.ParticipantAudioTrackId is { } trackId)
                {
                    await RefreshParticipantAudioTrackAggregateAsync(trackId, cancellationToken);
                }

                await _dbContext.SaveChangesAsync(cancellationToken);

                TryDeleteBackupFile(backupPath!);

                await TryRecordTrackerAsync(
                    async ct =>
                    {
                        await _postMeetingProcessingTracker!.StartStepAsync(
                            fragment.OrganizationId,
                            fragment.MeetingId,
                            PostMeetingProcessingStepType.AudioIngest,
                            message: "Uploading LiveKit egress backup audio to durable object storage.",
                            artifact: new PostMeetingArtifactLink("participant_audio_fragment", fragment.Id),
                            cancellationToken: ct);
                        await _postMeetingProcessingTracker.CompleteStepAsync(
                            fragment.OrganizationId,
                            fragment.MeetingId,
                            PostMeetingProcessingStepType.AudioIngest,
                            message: "Participant audio backup uploaded to durable object storage.",
                            artifact: new PostMeetingArtifactLink("participant_audio_fragment", fragment.Id),
                            cancellationToken: ct);
                    },
                    cancellationToken);

                _logger.LogInformation(
                    "Participant audio backup persisted. FragmentId={FragmentId} MeetingId={MeetingId} ObjectKey={ObjectKey} StorageLocation={StorageLocation}",
                    fragment.Id,
                    fragment.MeetingId,
                    fragment.StorageObjectKey,
                    fragment.StorageLocation);

                var readyEvent = await _participantAudioReadinessService.TryCreateReadyEventAsync(
                    fragment.MeetingId,
                    fragment.OrganizationId,
                    cancellationToken);

                if (readyEvent is not null)
                {
                    await _publisher.Publish(readyEvent, cancellationToken);
                }
            }
            catch (Exception ex)
            {
                fragment.StorageUploadLeaseExpiresAtUtc = null;
                fragment.FailureCode = "storage_upload_failed";
                fragment.FailureMessage = Truncate(ex.GetBaseException().Message, 2000);
                await _dbContext.SaveChangesAsync(cancellationToken);

                await TryRecordTrackerAsync(
                    async ct => await _postMeetingProcessingTracker!.RecordEventAsync(
                        fragment.OrganizationId,
                        fragment.MeetingId,
                        PostMeetingProcessingEventType.Error,
                        stepType: PostMeetingProcessingStepType.AudioIngest,
                        status: PostMeetingProcessingStatus.InProgress,
                        message: "Participant audio backup upload failed; Hangfire will retry while the backup file remains available.",
                        artifact: new PostMeetingArtifactLink("participant_audio_fragment", fragment.Id),
                        errorCode: fragment.FailureCode,
                        errorMessage: fragment.FailureMessage,
                        cancellationToken: ct),
                    cancellationToken);

                _logger.LogError(
                    ex,
                    "Participant audio backup upload failed. FragmentId={FragmentId} MeetingId={MeetingId} BackupPath={BackupPath} AttemptCount={AttemptCount}",
                    fragment.Id,
                    fragment.MeetingId,
                    backupPath,
                    fragment.StorageUploadAttemptCount);

                throw;
            }
        }

        private async Task TryRecordTrackerAsync(
            Func<CancellationToken, Task> recordAsync,
            CancellationToken cancellationToken)
        {
            if (_postMeetingProcessingTracker is null)
            {
                return;
            }

            try
            {
                await recordAsync(cancellationToken);
            }
            catch (Exception trackerEx)
            {
                _logger.LogWarning(
                    trackerEx,
                    "Post-meeting processing tracker update failed (best-effort).");
            }
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

        private void TryDeleteBackupFile(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to delete persisted egress backup file {Path}", path);
            }
        }

        private static string? ExtractObjectKeyFromPath(string? pathOrUrl)
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
            var tracksIndex = Array.FindIndex(
                segments,
                segment => segment.Equals("tracks", StringComparison.OrdinalIgnoreCase));

            return tracksIndex >= 0 ? string.Join('/', segments.Skip(tracksIndex)) : null;
        }

        private static string BuildFallbackObjectKey(ParticipantAudioFragment fragment)
        {
            if (fragment.SpeakerRole == ParticipantAudioFragmentSpeakerRole.Assistant)
            {
                var identity = LiveKitParticipantIdentity.SanitizeIdentityForObjectKey(
                    fragment.ParticipantIdentity ?? "assistant");
                return $"tracks/mtg:{fragment.MeetingId}/{identity}/track-{fragment.TrackSid}.ogg";
            }

            return $"tracks/mtg:{fragment.MeetingId}/user:{fragment.ParticipantUserId}/track-{fragment.TrackSid}.ogg";
        }

        private static string? FirstNonEmpty(params string?[] values)
            => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

        private static string Truncate(string value, int maxLength)
            => value.Length <= maxLength ? value : value[..maxLength];
    }
}
