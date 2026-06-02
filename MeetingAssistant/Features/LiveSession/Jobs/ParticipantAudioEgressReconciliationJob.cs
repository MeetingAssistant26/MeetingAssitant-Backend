using Hangfire;
using MediatR;
using MeetingAssistant.Features.LiveSession.Infrastructure;
using MeetingAssistant.Features.LiveSession.Models;
using MeetingAssistant.Features.LiveSession.Models.Events;
using MeetingAssistant.Features.LiveSession.Models.PostProcessing;
using MeetingAssistant.Features.LiveSession.Services;
using MeetingAssistant.Features.LiveSession.Services.PostProcessing;
using MeetingAssistant.Infrastructure.Persistence.DbContext;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MeetingAssistant.Features.LiveSession.Jobs
{
    public sealed class ParticipantAudioEgressReconciliationJob(
        ApplicationDbContext dbContext,
        IBackgroundJobClient backgroundJobClient,
        IPublisher publisher,
        ILogger<ParticipantAudioEgressReconciliationJob> logger,
        IOptions<LiveKitOptions>? liveKitOptions = null,
        IPostMeetingProcessingTracker? postMeetingProcessingTracker = null,
        IParticipantAudioReadinessService? participantAudioReadinessService = null)
    {
        private const int DefaultParticipantAudioIngestCeilingMinutes = 360;
        private static readonly TimeSpan RetryCadence = TimeSpan.FromSeconds(30);
        private const int BatchSize = 100;

        private readonly ApplicationDbContext _dbContext = dbContext;
        private readonly IBackgroundJobClient _backgroundJobClient = backgroundJobClient;
        private readonly IPublisher _publisher = publisher;
        private readonly ILogger<ParticipantAudioEgressReconciliationJob> _logger = logger;
        private readonly IPostMeetingProcessingTracker? _postMeetingProcessingTracker = postMeetingProcessingTracker;
        private readonly IParticipantAudioReadinessService _participantAudioReadinessService = participantAudioReadinessService
            ?? new ParticipantAudioReadinessService(dbContext);
        private readonly TimeSpan _participantAudioIngestCeiling = TimeSpan.FromMinutes(
            NormalizeParticipantAudioIngestCeilingMinutes(
                liveKitOptions?.Value.ParticipantAudioIngestCeilingMinutes));

        [DisableConcurrentExecution(timeoutInSeconds: 60)]
        public async Task RunAsync(CancellationToken cancellationToken = default)
        {
            var now = DateTime.UtcNow;
            await ReconcileEgressStartsAsync(now, cancellationToken);
            await ReconcileBackupUploadsAsync(now, cancellationToken);
        }

        private async Task ReconcileEgressStartsAsync(
            DateTime now,
            CancellationToken cancellationToken)
        {
            var retryBeforeUtc = now.Subtract(RetryCadence);

            var pendingStarts = await _dbContext.ParticipantAudioFragments
                .IgnoreQueryFilters()
                .Where(x => x.Status == ParticipantAudioFragmentStatus.Pending)
                .Where(x => x.EgressId == null)
                .Where(x => x.TrackPublishedAtUtc != null)
                .Where(x => x.StorageLocation == null)
                .Where(x => x.BackupStoragePath == null)
                .Where(x => x.EgressStartLeaseExpiresAtUtc == null || x.EgressStartLeaseExpiresAtUtc <= now)
                .Where(x => x.LastEgressStartAttemptAtUtc == null || x.LastEgressStartAttemptAtUtc <= retryBeforeUtc)
                .OrderBy(x => x.TrackPublishedAtUtc ?? x.CreatedAtUtc)
                .ThenBy(x => x.Id)
                .Take(BatchSize)
                .ToListAsync(cancellationToken);

            foreach (var fragment in pendingStarts)
            {
                var recoverableUntilUtc = (fragment.TrackPublishedAtUtc ?? fragment.CreatedAtUtc).Add(_participantAudioIngestCeiling);
                if (recoverableUntilUtc <= now)
                {
                    await MarkFragmentUnrecoverableAsync(
                        fragment,
                        now,
                        "egress_start_expired",
                        $"Participant audio egress start was not recoverable before the {_participantAudioIngestCeiling.TotalMinutes:0}-minute ceiling.",
                        cancellationToken);
                    continue;
                }

                var jobId = _backgroundJobClient.Enqueue<StartParticipantAudioEgressJob>(
                    job => job.RunAsync(fragment.Id, CancellationToken.None));

                if (_postMeetingProcessingTracker is not null)
                {
                    await _postMeetingProcessingTracker.MarkStepPendingAsync(
                        fragment.OrganizationId,
                        fragment.MeetingId,
                        PostMeetingProcessingStepType.AudioIngest,
                        message: "Reconciled missing participant audio egress start job.",
                        relatedHangfireJobId: jobId,
                        artifact: new PostMeetingArtifactLink("participant_audio_fragment", fragment.Id),
                        cancellationToken: cancellationToken);
                }

                _logger.LogWarning(
                    "Re-enqueued missing participant audio egress start job. FragmentId={FragmentId} MeetingId={MeetingId} TrackSid={TrackSid} AttemptCount={AttemptCount}",
                    fragment.Id,
                    fragment.MeetingId,
                    fragment.TrackSid,
                    fragment.EgressStartAttemptCount);
            }
        }

        private async Task ReconcileBackupUploadsAsync(
            DateTime now,
            CancellationToken cancellationToken)
        {
            var retryBeforeUtc = now.Subtract(RetryCadence);

            var pendingBackups = await _dbContext.ParticipantAudioFragments
                .IgnoreQueryFilters()
                .Where(x => x.Status == ParticipantAudioFragmentStatus.Pending)
                .Where(x => x.BackupStoragePath != null)
                .Where(x => x.StorageLocation == null)
                .Where(x => x.StorageUploadLeaseExpiresAtUtc == null || x.StorageUploadLeaseExpiresAtUtc <= now)
                .Where(x => x.LastStorageUploadAttemptAtUtc == null || x.LastStorageUploadAttemptAtUtc <= retryBeforeUtc)
                .OrderBy(x => x.BackupStorageAvailableAtUtc ?? x.EgressEndedAtUtc ?? x.UpdatedAtUtc)
                .ThenBy(x => x.Id)
                .Take(BatchSize)
                .ToListAsync(cancellationToken);

            foreach (var fragment in pendingBackups)
            {
                var jobId = _backgroundJobClient.Enqueue<PersistParticipantAudioFragmentJob>(
                    job => job.RunAsync(fragment.Id, CancellationToken.None));

                if (_postMeetingProcessingTracker is not null)
                {
                    await _postMeetingProcessingTracker.MarkStepPendingAsync(
                        fragment.OrganizationId,
                        fragment.MeetingId,
                        PostMeetingProcessingStepType.AudioIngest,
                        message: "Reconciled missing participant audio backup upload job.",
                        relatedHangfireJobId: jobId,
                        artifact: new PostMeetingArtifactLink("participant_audio_fragment", fragment.Id),
                        cancellationToken: cancellationToken);
                }

                _logger.LogWarning(
                    "Re-enqueued missing participant audio backup upload job. FragmentId={FragmentId} MeetingId={MeetingId} BackupPath={BackupPath} AttemptCount={AttemptCount}",
                    fragment.Id,
                    fragment.MeetingId,
                    fragment.BackupStoragePath,
                    fragment.StorageUploadAttemptCount);
            }
        }

        private async Task MarkFragmentUnrecoverableAsync(
            ParticipantAudioFragment fragment,
            DateTime now,
            string failureCode,
            string failureMessage,
            CancellationToken cancellationToken)
        {
            fragment.Status = ParticipantAudioFragmentStatus.Failed;
            fragment.FailedAtUtc = now;
            fragment.FailureCode = failureCode;
            fragment.FailureMessage = failureMessage;
            fragment.EgressStartLeaseExpiresAtUtc = null;
            fragment.StorageUploadLeaseExpiresAtUtc = null;

            if (fragment.ParticipantAudioTrackId is { } participantAudioTrackId)
            {
                await RefreshParticipantAudioTrackAggregateAsync(participantAudioTrackId, cancellationToken);
            }

            await _dbContext.SaveChangesAsync(cancellationToken);

            if (_postMeetingProcessingTracker is not null)
            {
                await _postMeetingProcessingTracker.RecordEventAsync(
                    fragment.OrganizationId,
                    fragment.MeetingId,
                    PostMeetingProcessingEventType.Error,
                    stepType: PostMeetingProcessingStepType.AudioIngest,
                    status: PostMeetingProcessingStatus.InProgress,
                    message: "Participant audio egress start is no longer recoverable; marking the fragment failed so downstream processing can continue with remaining audio.",
                    artifact: new PostMeetingArtifactLink("participant_audio_fragment", fragment.Id),
                    errorCode: failureCode,
                    errorMessage: failureMessage,
                    cancellationToken: cancellationToken);
            }

            _logger.LogError(
                "Participant audio egress start marked unrecoverable. FragmentId={FragmentId} MeetingId={MeetingId} TrackSid={TrackSid} AttemptCount={AttemptCount} FailureCode={FailureCode}",
                fragment.Id,
                fragment.MeetingId,
                fragment.TrackSid,
                fragment.EgressStartAttemptCount,
                failureCode);

            var readyEvent = await _participantAudioReadinessService.TryCreateReadyEventAsync(
                fragment.MeetingId,
                fragment.OrganizationId,
                cancellationToken);

            if (readyEvent is not null)
            {
                await _publisher.Publish(readyEvent, cancellationToken);
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

        private static int NormalizeParticipantAudioIngestCeilingMinutes(int? value)
        {
            return value is > 0 ? value.Value : DefaultParticipantAudioIngestCeilingMinutes;
        }
    }
}
