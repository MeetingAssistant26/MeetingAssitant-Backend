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
    public sealed class StartParticipantAudioEgressJob(
        ApplicationDbContext dbContext,
        IEgressService egressService,
        ILogger<StartParticipantAudioEgressJob> logger,
        IPostMeetingProcessingTracker? postMeetingProcessingTracker = null)
    {
        private static readonly TimeSpan StartLeaseDuration = TimeSpan.FromMinutes(2);

        private readonly ApplicationDbContext _dbContext = dbContext;
        private readonly IEgressService _egressService = egressService;
        private readonly ILogger<StartParticipantAudioEgressJob> _logger = logger;
        private readonly IPostMeetingProcessingTracker? _postMeetingProcessingTracker = postMeetingProcessingTracker;

        [AutomaticRetry(Attempts = 5)]
        public async Task RunAsync(Guid fragmentId, CancellationToken cancellationToken = default)
        {
            var now = DateTime.UtcNow;
            var leaseUntil = now.Add(StartLeaseDuration);

            var claimed = await _dbContext.ParticipantAudioFragments
                .IgnoreQueryFilters()
                .Where(x => x.Id == fragmentId)
                .Where(x => x.Status == ParticipantAudioFragmentStatus.Pending)
                .Where(x => x.EgressId == null)
                .Where(x => x.EgressStartLeaseExpiresAtUtc == null || x.EgressStartLeaseExpiresAtUtc <= now)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(x => x.EgressStartLeaseExpiresAtUtc, leaseUntil)
                        .SetProperty(x => x.LastEgressStartAttemptAtUtc, now)
                        .SetProperty(x => x.EgressStartAttemptCount, x => x.EgressStartAttemptCount + 1)
                        .SetProperty(x => x.UpdatedAtUtc, now),
                    cancellationToken);

            if (claimed == 0)
            {
                var snapshot = await _dbContext.ParticipantAudioFragments
                    .IgnoreQueryFilters()
                    .AsNoTracking()
                    .Where(x => x.Id == fragmentId)
                    .Select(x => new
                    {
                        x.Status,
                        x.EgressId,
                        x.EgressStartLeaseExpiresAtUtc
                    })
                    .FirstOrDefaultAsync(cancellationToken);

                if (snapshot is not null
                    && snapshot.Status == ParticipantAudioFragmentStatus.Pending
                    && snapshot.EgressId == null
                    && snapshot.EgressStartLeaseExpiresAtUtc > now)
                {
                    _logger.LogWarning(
                        "Participant audio egress start blocked by active lease. FragmentId={FragmentId} LeaseExpiresAtUtc={LeaseExpiresAtUtc}",
                        fragmentId,
                        snapshot.EgressStartLeaseExpiresAtUtc);

                    throw new InvalidOperationException(
                        $"Participant audio fragment {fragmentId} egress start is already leased until {snapshot.EgressStartLeaseExpiresAtUtc:O}.");
                }

                _logger.LogInformation(
                    "Participant audio egress start skipped because fragment is already claimed or terminal. FragmentId={FragmentId}",
                    fragmentId);
                return;
            }

            var fragment = await _dbContext.ParticipantAudioFragments
                .IgnoreQueryFilters()
                .FirstAsync(x => x.Id == fragmentId, cancellationToken);

            var roomName = $"mtg:{fragment.MeetingId}";
            var participantIdentity = !string.IsNullOrWhiteSpace(fragment.ParticipantIdentity)
                ? fragment.ParticipantIdentity
                : fragment.ParticipantUserId.HasValue
                    ? LiveKitParticipantIdentity.BuildHumanParticipantIdentity(fragment.ParticipantUserId.Value)
                    : throw new InvalidOperationException(
                        $"Participant audio fragment {fragment.Id} has no resolvable participant identity.");

            try
            {
                var result = await _egressService.StartTrackEgressAsync(
                    fragment.MeetingId,
                    roomName,
                    fragment.TrackSid,
                    participantIdentity,
                    cancellationToken);

                fragment.EgressId = result.EgressId;
                fragment.EgressStartedAtUtc ??= DateTime.UtcNow;
                fragment.StorageObjectKey = string.IsNullOrWhiteSpace(result.StorageObjectKey)
                    ? fragment.StorageObjectKey
                    : result.StorageObjectKey;
                fragment.EgressStartLeaseExpiresAtUtc = null;
                fragment.FailedAtUtc = null;
                fragment.FailureCode = null;
                fragment.FailureMessage = null;

                await _dbContext.SaveChangesAsync(cancellationToken);

                await TryRecordTrackerAsync(
                    async ct =>
                    {
                        await _postMeetingProcessingTracker!.StartStepAsync(
                            fragment.OrganizationId,
                            fragment.MeetingId,
                            PostMeetingProcessingStepType.AudioIngest,
                            message: "Participant audio egress start attempt running.",
                            artifact: new PostMeetingArtifactLink("participant_audio_fragment", fragment.Id),
                            cancellationToken: ct);
                        await _postMeetingProcessingTracker.MarkStepPendingAsync(
                            fragment.OrganizationId,
                            fragment.MeetingId,
                            PostMeetingProcessingStepType.AudioIngest,
                            message: "Participant audio egress started; waiting for egress completion/storage.",
                            artifact: new PostMeetingArtifactLink("participant_audio_fragment", fragment.Id),
                            cancellationToken: ct);
                    },
                    cancellationToken);

                _logger.LogInformation(
                    "Participant audio egress started. FragmentId={FragmentId} MeetingId={MeetingId} TrackSid={TrackSid} EgressId={EgressId} StorageObjectKey={StorageObjectKey}",
                    fragment.Id,
                    fragment.MeetingId,
                    fragment.TrackSid,
                    fragment.EgressId,
                    fragment.StorageObjectKey);
            }
            catch (Exception ex)
            {
                fragment.EgressStartLeaseExpiresAtUtc = null;
                fragment.FailureCode = "egress_start_failed";
                fragment.FailureMessage = Truncate(ex.GetBaseException().Message, 2000);
                await _dbContext.SaveChangesAsync(cancellationToken);

                await TryRecordTrackerAsync(
                    async ct => await _postMeetingProcessingTracker!.RecordEventAsync(
                        fragment.OrganizationId,
                        fragment.MeetingId,
                        PostMeetingProcessingEventType.Error,
                        stepType: PostMeetingProcessingStepType.AudioIngest,
                        status: PostMeetingProcessingStatus.InProgress,
                        message: "Participant audio egress start attempt failed; Hangfire will retry while the fragment remains recoverable.",
                        artifact: new PostMeetingArtifactLink("participant_audio_fragment", fragment.Id),
                        errorCode: fragment.FailureCode,
                        errorMessage: fragment.FailureMessage,
                        cancellationToken: ct),
                    cancellationToken);

                _logger.LogError(
                    ex,
                    "Participant audio egress start failed. FragmentId={FragmentId} MeetingId={MeetingId} TrackSid={TrackSid} AttemptCount={AttemptCount}",
                    fragment.Id,
                    fragment.MeetingId,
                    fragment.TrackSid,
                    fragment.EgressStartAttemptCount);

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

        private static string Truncate(string value, int maxLength)
            => value.Length <= maxLength ? value : value[..maxLength];
    }
}
