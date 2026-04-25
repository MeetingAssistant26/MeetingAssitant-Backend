using MeetingAssistant.Features.LiveSession.Models;
using MeetingAssistant.Features.LiveSession.Services;
using MeetingAssistant.Infrastructure.Persistence.DbContext;
using Microsoft.EntityFrameworkCore;

namespace MeetingAssistant.Features.LiveSession.Jobs
{
    public class DownloadRecordingJob(
        ApplicationDbContext dbContext,
        IStorageService storageService,
        ILogger<DownloadRecordingJob> logger)
    {
        private readonly ApplicationDbContext _dbContext = dbContext;
        private readonly IStorageService _storageService = storageService;
        private readonly ILogger<DownloadRecordingJob> _logger = logger;

        public async Task RunAsync(Guid meetingId, string sourceCloudUrl, CancellationToken cancellationToken = default)
        {
            var recording = await _dbContext.ParticipantAudioTracks
                .FirstOrDefaultAsync(x => x.MeetingId == meetingId, cancellationToken);

            if (recording == null)
            {
                _logger.LogWarning(
                    "Download recording skipped. MeetingId={MeetingId} Reason={Reason}",
                    meetingId,
                    "recording_row_missing");
                return;
            }

            if (recording.Status is ParticipantAudioTrackStatus.Available or ParticipantAudioTrackStatus.Failed)
            {
                _logger.LogInformation(
                    "Download recording skipped. MeetingId={MeetingId} StatusTransition={StatusTransition} TargetKey={TargetKey} SizeBytes={SizeBytes}",
                    meetingId,
                    $"{recording.Status}->{recording.Status}",
                    recording.StorageObjectKey,
                    (long?)null);
                return;
            }

            var targetKey = $"recordings/{meetingId}.mp4";
            var previousStatus = recording.Status;

            try
            {
                var sizeBytes = await _storageService.UploadFromUrlAsync(sourceCloudUrl, targetKey, cancellationToken);

                recording.StorageObjectKey = targetKey;
                recording.Status = ParticipantAudioTrackStatus.Available;
                await _dbContext.SaveChangesAsync(cancellationToken);

                _logger.LogInformation(
                    "Download recording completed. MeetingId={MeetingId} StatusTransition={StatusTransition} TargetKey={TargetKey} SizeBytes={SizeBytes}",
                    meetingId,
                    $"{previousStatus}->{recording.Status}",
                    targetKey,
                    sizeBytes);
            }
            catch (Exception ex)
            {
                recording.Status = ParticipantAudioTrackStatus.Failed;
                await _dbContext.SaveChangesAsync(cancellationToken);

                _logger.LogError(
                    ex,
                    "Download recording failed. MeetingId={MeetingId} StatusTransition={StatusTransition} TargetKey={TargetKey} SizeBytes={SizeBytes}",
                    meetingId,
                    $"{previousStatus}->{recording.Status}",
                    targetKey,
                    (long?)null);
            }
        }
    }
}
