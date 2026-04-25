using MeetingAssistant.Features.LiveSession.Models;
using MeetingAssistant.Features.LiveSession.Models.Events;
using MeetingAssistant.Features.LiveSession.Services;
using MeetingAssistant.Infrastructure.Persistence.DbContext;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace MeetingAssistant.Features.LiveSession.Jobs
{
    public class IngestParticipantAudioJob(
        ApplicationDbContext dbContext,
        IStorageService storageService,
        IPublisher publisher,
        ILogger<IngestParticipantAudioJob> logger)
    {
        private const string UniqueViolationSqlState = "23505";

        private readonly ApplicationDbContext _dbContext = dbContext;
        private readonly IStorageService _storageService = storageService;
        private readonly IPublisher _publisher = publisher;
        private readonly ILogger<IngestParticipantAudioJob> _logger = logger;

        public async Task RunAsync(Guid trackId, string egressSourceUrl, CancellationToken cancellationToken = default)
        {
            var track = await _dbContext.ParticipantAudioTracks
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
                    "Participant audio ingest skipped. TrackId={TrackId} StatusTransition={StatusTransition} TargetKey={TargetKey} SizeBytes={SizeBytes}",
                    trackId,
                    $"{track.Status}->{track.Status}",
                    track.StorageObjectKey,
                    (long?)null);
                return;
            }

            var previousStatus = track.Status;
            var targetKey = $"tracks/{track.MeetingId}/{track.ParticipantUserId}.ogg";

            try
            {
                var sizeBytes = await _storageService.UploadFromUrlAsync(egressSourceUrl, targetKey, cancellationToken);

                track.StorageObjectKey = targetKey;
                track.SizeBytes = sizeBytes;
                track.Status = ParticipantAudioTrackStatus.Available;
                var readyEvent = await PersistTerminalStatusAndTryCreateReadyEventAsync(track, cancellationToken);

                _logger.LogInformation(
                    "Participant audio ingest completed. TrackId={TrackId} MeetingId={MeetingId} StatusTransition={StatusTransition} TargetKey={TargetKey} SizeBytes={SizeBytes}",
                    trackId,
                    track.MeetingId,
                    $"{previousStatus}->{track.Status}",
                    targetKey,
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
                    "Participant audio ingest failed. TrackId={TrackId} MeetingId={MeetingId} StatusTransition={StatusTransition} TargetKey={TargetKey} SizeBytes={SizeBytes}",
                    trackId,
                    track.MeetingId,
                    $"{previousStatus}->{track.Status}",
                    targetKey,
                    (long?)null);

                if (readyEvent is not null)
                {
                    await _publisher.Publish(readyEvent, cancellationToken);
                }
            }
        }

        private async Task<ParticipantAudioReadyEvent?> PersistTerminalStatusAndTryCreateReadyEventAsync(
            ParticipantAudioTrack track,
            CancellationToken cancellationToken)
        {
            // Save terminal status first; the SessionEvent insert below is allowed to fail under
            // a concurrent winner without rolling back the track update.
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
