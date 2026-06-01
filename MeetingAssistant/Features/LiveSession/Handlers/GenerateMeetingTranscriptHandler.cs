using Hangfire;
using MediatR;
using MeetingAssistant.Features.LiveSession.Jobs;
using MeetingAssistant.Features.LiveSession.Models.Events;
using MeetingAssistant.Features.LiveSession.Models.PostProcessing;
using MeetingAssistant.Features.LiveSession.Services.PostProcessing;

namespace MeetingAssistant.Features.LiveSession.Handlers
{
    public class GenerateMeetingTranscriptHandler(
        IBackgroundJobClient backgroundJobClient,
        IPostMeetingProcessingTracker? postMeetingProcessingTracker = null) : INotificationHandler<ParticipantAudioReadyEvent>
    {
        private readonly IBackgroundJobClient _backgroundJobClient = backgroundJobClient;
        private readonly IPostMeetingProcessingTracker? _postMeetingProcessingTracker = postMeetingProcessingTracker;

        public async Task Handle(ParticipantAudioReadyEvent notification, CancellationToken cancellationToken)
        {
            var jobId = _backgroundJobClient.Enqueue<GenerateMeetingTranscriptJob>(
                job => job.RunAsync(
                    notification.MeetingId,
                    notification.OrganizationId,
                    CancellationToken.None));

            if (_postMeetingProcessingTracker is not null)
            {
                await _postMeetingProcessingTracker.MarkStepPendingAsync(
                    notification.OrganizationId,
                    notification.MeetingId,
                    PostMeetingProcessingStepType.Stt,
                    message: "STT transcription job enqueued.",
                    relatedHangfireJobId: jobId,
                    cancellationToken: cancellationToken);
            }
        }
    }
}
