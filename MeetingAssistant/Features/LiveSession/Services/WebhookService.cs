using Hangfire;
using Livekit.Server.Sdk.Dotnet;
using MediatR;
using MeetingAssistant.Features.LiveSession.Infrastructure;
using MeetingAssistant.Features.LiveSession.Jobs;
using MeetingAssistant.Features.LiveSession.Models;
using MeetingAssistant.Features.LiveSession.Models.Events;
using MeetingAssistant.Features.LiveSession.Models.PostProcessing;
using MeetingAssistant.Features.LiveSession.Services.PostProcessing;
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
        ILogger<WebhookService> logger,
        IPostMeetingProcessingTracker? postMeetingProcessingTracker = null,
        IParticipantAudioReadinessService? participantAudioReadinessService = null,
        IPublisher? publisher = null) : IWebhookService
    {
        private const string UniqueViolationSqlState = "23505";

        private readonly ApplicationDbContext _dbContext = dbContext;
        private readonly IBackgroundJobClient _backgroundJobClient = backgroundJobClient;
        private readonly LiveKitOptions _options = options.Value;
        private readonly ILogger<WebhookService> _logger = logger;
        private readonly IPostMeetingProcessingTracker? _postMeetingProcessingTracker = postMeetingProcessingTracker;
        private readonly IParticipantAudioReadinessService _participantAudioReadinessService = participantAudioReadinessService
            ?? new ParticipantAudioReadinessService(dbContext);
        private readonly IPublisher? _publisher = publisher;

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
            var backupPersistEnqueues = new List<Guid>();
            var egressStartEnqueues = new List<Guid>();
            var discoveredFragmentIds = new List<Guid>();

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
                        var egressDetails = ExtractEgressDetails(
                            rawPayload,
                            webhookEvent.EgressInfo?.BackupStorageUsed == true);

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

                            var isAssistant = existingFragment?.SpeakerRole == ParticipantAudioFragmentSpeakerRole.Assistant
                                || LiveKitParticipantIdentity.IsAssistantIdentity(fileIdentity);

                            Guid? resolvedParticipantUserId;
                            Guid? trackId = null;

                            if (isAssistant)
                            {
                                resolvedParticipantUserId = null;
                            }
                            else
                            {
                                resolvedParticipantUserId = existingFragment?.ParticipantUserId
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
                            }

                            var backupStoragePath = ResolveBackupStoragePath(
                                egressDetails.BackupStorageUsed,
                                fileName,
                                fileLocation,
                                _options.EgressBackupStoragePath);
                            var hasRecoverableBackup = !string.IsNullOrWhiteSpace(backupStoragePath);

                            var status = (egressOk && !string.IsNullOrWhiteSpace(fileLocation)) || hasRecoverableBackup
                                ? ParticipantAudioFragmentStatus.Pending
                                : ParticipantAudioFragmentStatus.Failed;

                            if (!isAssistant)
                            {
                                var aggregateStatus = status == ParticipantAudioFragmentStatus.Failed
                                    ? ParticipantAudioTrackStatus.Failed
                                    : ParticipantAudioTrackStatus.Pending;
                                trackId = await UpsertParticipantAudioTrackAsync(
                                    meeting.Id,
                                    meeting.OrganizationId,
                                    resolvedParticipantUserId!.Value,
                                    aggregateStatus,
                                    cancellationToken);
                            }

                            var participantIdentity = isAssistant
                                ? fileIdentity
                                : LiveKitParticipantIdentity.BuildHumanParticipantIdentity(resolvedParticipantUserId!.Value);

                            var fragment = await UpsertParticipantAudioFragmentAsync(
                                meeting.Id,
                                meeting.OrganizationId,
                                isAssistant
                                    ? ParticipantAudioFragmentSpeakerRole.Assistant
                                    : ParticipantAudioFragmentSpeakerRole.Participant,
                                resolvedParticipantUserId,
                                trackId,
                                participantIdentity,
                                isAssistant ? LiveKitParticipantIdentity.AssistantDisplayName : null,
                                trackSid,
                                egressDetails.EgressId,
                                fileName,
                                hasRecoverableBackup ? null : fileLocation,
                                FirstNonEmpty(
                                    ExtractObjectKeyFromEgressPath(fileLocation),
                                    ExtractObjectKeyFromEgressPath(fileName)),
                                backupStoragePath,
                                hasRecoverableBackup ? occurredAtUtc : null,
                                status,
                                file?.Size,
                                egressDetails.DurationSeconds,
                                existingFragment,
                                trackPublishedAtUtc: null,
                                egressStartedAtUtc: egressDetails.StartedAtUtc,
                                egressEndedAtUtc: egressDetails.EndedAtUtc ?? occurredAtUtc,
                                failureCode: status == ParticipantAudioFragmentStatus.Failed ? egressDetails.FailureCode ?? "egress_failed" : null,
                                failureMessage: status == ParticipantAudioFragmentStatus.Failed ? egressDetails.FailureMessage ?? $"LiveKit egress ended with status {webhookEvent.EgressInfo?.Status}" : null,
                                cancellationToken);
                            discoveredFragmentIds.Add(fragment.Id);

                            if (status == ParticipantAudioFragmentStatus.Failed && trackId.HasValue)
                            {
                                await RefreshParticipantAudioTrackAggregateAsync(trackId.Value, cancellationToken);
                            }

                            if (status == ParticipantAudioFragmentStatus.Pending)
                            {
                                if (hasRecoverableBackup)
                                {
                                    backupPersistEnqueues.Add(fragment.Id);
                                }
                                else
                                {
                                    ingestEnqueues.Add((fragment.Id, fileLocation!, file?.Size));
                                }
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
                            if (LiveKitParticipantIdentity.IsAssistantParticipant(identity, rawPayload))
                            {
                                var assistantFragment = await UpsertParticipantAudioFragmentAsync(
                                    meeting.Id,
                                    meeting.OrganizationId,
                                    ParticipantAudioFragmentSpeakerRole.Assistant,
                                    participantUserId: null,
                                    participantAudioTrackId: null,
                                    participantIdentity: identity,
                                    speakerDisplayName: LiveKitParticipantIdentity.AssistantDisplayName,
                                    trackSid,
                                    egressId: null,
                                    fileName: null,
                                    storageLocation: null,
                                    storageObjectKey: null,
                                    backupStoragePath: null,
                                    backupStorageAvailableAtUtc: null,
                                    status: ParticipantAudioFragmentStatus.Pending,
                                    sizeBytes: null,
                                    durationSeconds: null,
                                    existingFragment: null,
                                    trackPublishedAtUtc: occurredAtUtc,
                                    egressStartedAtUtc: null,
                                    egressEndedAtUtc: null,
                                    failureCode: null,
                                    failureMessage: null,
                                    cancellationToken);
                                discoveredFragmentIds.Add(assistantFragment.Id);
                                egressStartEnqueues.Add(assistantFragment.Id);
                                break;
                            }

                            var resolvedParticipantUserId = participantUserId
                                ?? await ResolveParticipantUserIdAsync(meeting.Id, identity, cancellationToken);
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
                                ParticipantAudioFragmentSpeakerRole.Participant,
                                resolvedParticipantUserId,
                                trackId,
                                LiveKitParticipantIdentity.BuildHumanParticipantIdentity(resolvedParticipantUserId.Value),
                                speakerDisplayName: null,
                                trackSid,
                                egressId: null,
                                fileName: null,
                                storageLocation: null,
                                storageObjectKey: null,
                                backupStoragePath: null,
                                backupStorageAvailableAtUtc: null,
                                status: ParticipantAudioFragmentStatus.Pending,
                                sizeBytes: null,
                                durationSeconds: null,
                                existingFragment: null,
                                trackPublishedAtUtc: occurredAtUtc,
                                egressStartedAtUtc: null,
                                egressEndedAtUtc: null,
                                failureCode: null,
                                failureMessage: null,
                                cancellationToken);
                            discoveredFragmentIds.Add(fragment.Id);

                            egressStartEnqueues.Add(fragment.Id);
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

            foreach (var fragmentId in egressStartEnqueues)
            {
                var jobId = _backgroundJobClient.Enqueue<StartParticipantAudioEgressJob>(
                    job => job.RunAsync(fragmentId, CancellationToken.None));

                if (_postMeetingProcessingTracker is not null)
                {
                    await _postMeetingProcessingTracker.MarkStepPendingAsync(
                        meeting.OrganizationId,
                        meeting.Id,
                        PostMeetingProcessingStepType.AudioIngest,
                        message: "Participant audio egress start job enqueued.",
                        relatedHangfireJobId: jobId,
                        artifact: new PostMeetingArtifactLink("participant_audio_fragment", fragmentId),
                        cancellationToken: cancellationToken);
                }
            }

            foreach (var (fragmentId, s3LocationUrl, sizeBytes) in ingestEnqueues)
            {
                var jobId = _backgroundJobClient.Enqueue<IngestParticipantAudioJob>(
                    job => job.RunFragmentAsync(fragmentId, s3LocationUrl, sizeBytes, CancellationToken.None));

                if (_postMeetingProcessingTracker is not null)
                {
                    await _postMeetingProcessingTracker.MarkStepPendingAsync(
                        meeting.OrganizationId,
                        meeting.Id,
                        PostMeetingProcessingStepType.AudioIngest,
                        message: "Participant audio fragment ingest job enqueued.",
                        relatedHangfireJobId: jobId,
                        artifact: new PostMeetingArtifactLink("participant_audio_fragment", fragmentId),
                        cancellationToken: cancellationToken);
                }
            }

            foreach (var fragmentId in backupPersistEnqueues)
            {
                var jobId = _backgroundJobClient.Enqueue<PersistParticipantAudioFragmentJob>(
                    job => job.RunAsync(fragmentId, CancellationToken.None));

                if (_postMeetingProcessingTracker is not null)
                {
                    await _postMeetingProcessingTracker.MarkStepPendingAsync(
                        meeting.OrganizationId,
                        meeting.Id,
                        PostMeetingProcessingStepType.AudioIngest,
                        message: "Participant audio backup persist job enqueued.",
                        relatedHangfireJobId: jobId,
                        artifact: new PostMeetingArtifactLink("participant_audio_fragment", fragmentId),
                        cancellationToken: cancellationToken);
                }
            }

            if (_postMeetingProcessingTracker is not null)
            {
                if (eventType == SessionEventType.RoomFinished)
                {
                    await _postMeetingProcessingTracker.CompleteStepAsync(
                        meeting.OrganizationId,
                        meeting.Id,
                        PostMeetingProcessingStepType.RoomCompleted,
                        message: "LiveKit room completion webhook processed.",
                        cancellationToken: cancellationToken);
                }

                if (discoveredFragmentIds.Count > 0)
                {
                    await _postMeetingProcessingTracker.CompleteStepAsync(
                        meeting.OrganizationId,
                        meeting.Id,
                        PostMeetingProcessingStepType.FragmentDiscovery,
                        message: $"Discovered {discoveredFragmentIds.Count} participant audio fragment(s).",
                        artifact: new PostMeetingArtifactLink("participant_audio_fragment", ArtifactIds: discoveredFragmentIds),
                        cancellationToken: cancellationToken);
                }
            }

            await PublishParticipantAudioReadyIfReadyAsync(
                meeting.Id,
                meeting.OrganizationId,
                cancellationToken);

            return Result.Success();
        }

        private async Task PublishParticipantAudioReadyIfReadyAsync(
            Guid meetingId,
            Guid organizationId,
            CancellationToken cancellationToken)
        {
            var readyEvent = await _participantAudioReadinessService.TryCreateReadyEventAsync(
                meetingId,
                organizationId,
                cancellationToken);

            if (readyEvent is not null && _publisher is not null)
            {
                await _publisher.Publish(readyEvent, cancellationToken);
            }
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
            ParticipantAudioFragmentSpeakerRole speakerRole,
            Guid? participantUserId,
            Guid? participantAudioTrackId,
            string? participantIdentity,
            string? speakerDisplayName,
            string? trackSid,
            string? egressId,
            string? fileName,
            string? storageLocation,
            string? storageObjectKey,
            string? backupStoragePath,
            DateTime? backupStorageAvailableAtUtc,
            ParticipantAudioFragmentStatus status,
            long? sizeBytes,
            double? durationSeconds,
            ParticipantAudioFragment? existingFragment,
            DateTime? trackPublishedAtUtc,
            DateTime? egressStartedAtUtc,
            DateTime? egressEndedAtUtc,
            string? failureCode,
            string? failureMessage,
            CancellationToken cancellationToken)
        {
            var identitySeed = participantIdentity
                ?? (participantUserId.HasValue
                    ? LiveKitParticipantIdentity.BuildHumanParticipantIdentity(participantUserId.Value)
                    : "unknown");
            var effectiveTrackSid = trackSid
                ?? ResolveEgressTrackSid(null, fileName, storageLocation, egressId)
                ?? $"egress:{HashExternalEventSignature($"{meetingId}:{identitySeed}:{egressId}:{storageLocation}:{fileName}")}";

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
                    SpeakerRole = speakerRole,
                    ParticipantUserId = participantUserId,
                    ParticipantIdentity = participantIdentity,
                    SpeakerDisplayName = speakerDisplayName,
                    ParticipantAudioTrackId = participantAudioTrackId,
                    TrackSid = effectiveTrackSid
                };

                _dbContext.ParticipantAudioFragments.Add(fragment);
            }

            fragment.OrganizationId = organizationId;
            fragment.SpeakerRole = speakerRole;
            fragment.ParticipantUserId = participantUserId;
            fragment.ParticipantIdentity = string.IsNullOrWhiteSpace(participantIdentity)
                ? fragment.ParticipantIdentity
                : participantIdentity;
            fragment.SpeakerDisplayName = string.IsNullOrWhiteSpace(speakerDisplayName)
                ? fragment.SpeakerDisplayName
                : speakerDisplayName;
            fragment.ParticipantAudioTrackId = participantAudioTrackId ?? fragment.ParticipantAudioTrackId;
            fragment.EgressId = string.IsNullOrWhiteSpace(egressId) ? fragment.EgressId : egressId;
            fragment.StorageLocation = string.IsNullOrWhiteSpace(storageLocation) ? fragment.StorageLocation : storageLocation;
            fragment.StorageObjectKey = string.IsNullOrWhiteSpace(storageObjectKey) ? fragment.StorageObjectKey : storageObjectKey;
            fragment.BackupStoragePath = string.IsNullOrWhiteSpace(backupStoragePath) ? fragment.BackupStoragePath : backupStoragePath;
            fragment.BackupStorageAvailableAtUtc = backupStorageAvailableAtUtc ?? fragment.BackupStorageAvailableAtUtc;
            fragment.SizeBytes = sizeBytes ?? fragment.SizeBytes;
            if (durationSeconds.HasValue && durationSeconds.Value > 0)
            {
                fragment.DurationSeconds = durationSeconds.Value;
            }
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

        private static EgressWebhookDetails ExtractEgressDetails(
            string rawPayload,
            bool backupStorageUsedFromSdk = false)
        {
            if (string.IsNullOrWhiteSpace(rawPayload))
            {
                return new EgressWebhookDetails(null, null, null, null, null, null, null, backupStorageUsedFromSdk);
            }

            try
            {
                using var document = JsonDocument.Parse(rawPayload);
                if (!document.RootElement.TryGetProperty("egressInfo", out var egressInfo)
                    || egressInfo.ValueKind != JsonValueKind.Object)
                {
                    return new EgressWebhookDetails(null, null, null, null, null, null, null, backupStorageUsedFromSdk);
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
                var backupStorageUsed = backupStorageUsedFromSdk
                    || GetBool(egressInfo, "backupStorageUsed")
                    || GetBool(egressInfo, "backup_storage_used");
                var durationSeconds = ResolveDurationSeconds(egressInfo, startedAtUtc, endedAtUtc);

                if (!durationSeconds.HasValue
                    && egressInfo.TryGetProperty("fileResults", out var fileResults)
                    && fileResults.ValueKind == JsonValueKind.Array)
                {
                    foreach (var file in fileResults.EnumerateArray())
                    {
                        durationSeconds = ResolveDurationSeconds(file, startedAtUtc, endedAtUtc);
                        if (durationSeconds.HasValue)
                        {
                            break;
                        }
                    }
                }

                return new EgressWebhookDetails(
                    egressId,
                    trackSid,
                    startedAtUtc,
                    endedAtUtc,
                    failureCode,
                    failureMessage,
                    durationSeconds,
                    backupStorageUsed);
            }
            catch (JsonException)
            {
                return new EgressWebhookDetails(null, null, null, null, null, null, null, backupStorageUsedFromSdk);
            }
        }

        private static double? ResolveDurationSeconds(
            JsonElement element,
            DateTime? startedAtUtc,
            DateTime? endedAtUtc)
        {
            var fromPayload = TryGetDurationSeconds(element);
            if (fromPayload.HasValue)
            {
                return fromPayload.Value;
            }

            if (startedAtUtc.HasValue && endedAtUtc.HasValue)
            {
                var spanSeconds = (endedAtUtc.Value - startedAtUtc.Value).TotalSeconds;
                if (spanSeconds > 0)
                {
                    return spanSeconds;
                }
            }

            return null;
        }

        private static double? TryGetDurationSeconds(JsonElement element)
        {
            foreach (var propertyName in DurationPropertyNames)
            {
                if (!element.TryGetProperty(propertyName, out var property))
                {
                    continue;
                }

                if (TryConvertDurationProperty(property, propertyName, out var seconds))
                {
                    return seconds;
                }
            }

            return null;
        }

        private static bool TryConvertDurationProperty(
            JsonElement property,
            string propertyName,
            out double seconds)
        {
            seconds = 0;
            double rawValue;

            switch (property.ValueKind)
            {
                case JsonValueKind.Number:
                    rawValue = property.GetDouble();
                    break;
                case JsonValueKind.String when double.TryParse(property.GetString(), out var parsed):
                    rawValue = parsed;
                    break;
                default:
                    return false;
            }

            if (rawValue <= 0)
            {
                return false;
            }

            seconds = propertyName.Contains("ns", StringComparison.OrdinalIgnoreCase)
                ? rawValue / 1_000_000_000d
                : propertyName.Contains("ms", StringComparison.OrdinalIgnoreCase)
                    ? rawValue / 1_000d
                    : string.Equals(propertyName, "duration", StringComparison.OrdinalIgnoreCase)
                      && rawValue >= 1_000_000_000d
                        ? rawValue / 1_000_000_000d
                        : rawValue;

            return seconds > 0;
        }

        private static readonly string[] DurationPropertyNames =
        [
            "durationSeconds",
            "duration_seconds",
            "duration",
            "durationMs",
            "duration_ms",
            "durationNs",
            "duration_ns"
        ];

        private static string? GetString(JsonElement element, string propertyName)
        {
            return element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
                ? property.GetString()
                : null;
        }

        private static bool GetBool(JsonElement element, string propertyName)
        {
            return element.TryGetProperty(propertyName, out var property)
                && property.ValueKind == JsonValueKind.True;
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

        private static string? ResolveBackupStoragePath(
            bool backupStorageUsed,
            string? fileName,
            string? fileLocation,
            string? backupStorageRoot)
        {
            if (backupStorageUsed)
            {
                var explicitLocalPath = FirstNonEmpty(
                    IsLocalFilePath(fileLocation) ? fileLocation : null,
                    IsLocalFilePath(fileName) ? fileName : null);

                if (!string.IsNullOrWhiteSpace(explicitLocalPath))
                {
                    return explicitLocalPath;
                }

                var objectKey = FirstNonEmpty(
                    ExtractObjectKeyFromEgressPath(fileLocation),
                    ExtractObjectKeyFromEgressPath(fileName));

                if (!string.IsNullOrWhiteSpace(backupStorageRoot)
                    && !string.IsNullOrWhiteSpace(objectKey))
                {
                    return Path.Combine(
                        backupStorageRoot,
                        objectKey.Replace('/', Path.DirectorySeparatorChar));
                }

                return FirstNonEmpty(fileLocation, fileName);
            }

            return IsLocalFilePath(fileLocation) ? fileLocation : null;
        }

        private static bool IsLocalFilePath(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            if (Uri.TryCreate(value, UriKind.Absolute, out var uri))
            {
                return uri.IsFile;
            }

            return Path.IsPathRooted(value);
        }

        private static string? ExtractObjectKeyFromEgressPath(string? pathOrUrl)
        {
            if (string.IsNullOrWhiteSpace(pathOrUrl))
            {
                return null;
            }

            string path;
            if (Uri.TryCreate(pathOrUrl, UriKind.Absolute, out var uri))
            {
                path = uri.AbsolutePath.TrimStart('/');
            }
            else
            {
                path = pathOrUrl.TrimStart('/');
            }

            var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 0)
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

            return Uri.TryCreate(pathOrUrl, UriKind.Absolute, out _) && segments.Length >= 2
                ? string.Join('/', segments.Skip(1))
                : null;
        }

        private sealed record EgressWebhookDetails(
            string? EgressId,
            string? TrackSid,
            DateTime? StartedAtUtc,
            DateTime? EndedAtUtc,
            string? FailureCode,
            string? FailureMessage,
            double? DurationSeconds,
            bool BackupStorageUsed);

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
