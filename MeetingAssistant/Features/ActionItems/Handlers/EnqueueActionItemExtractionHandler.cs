using Hangfire;
using MediatR;
using MeetingAssistant.Features.ActionItems.Jobs;
using MeetingAssistant.Features.LiveSession.Models.Events;
using MeetingAssistant.Features.LiveSession.Models.PostProcessing;
using MeetingAssistant.Features.LiveSession.Services.PostProcessing;

namespace MeetingAssistant.Features.ActionItems.Handlers
{
    public class EnqueueActionItemExtractionHandler(
        IBackgroundJobClient backgroundJobClient,
        IPostMeetingProcessingTracker? postMeetingProcessingTracker = null) : INotificationHandler<MeetingTranscriptReadyEvent>
    {
        private readonly IBackgroundJobClient _backgroundJobClient = backgroundJobClient;
        private readonly IPostMeetingProcessingTracker? _postMeetingProcessingTracker = postMeetingProcessingTracker;

        public async Task Handle(MeetingTranscriptReadyEvent notification, CancellationToken cancellationToken)
        {
            var jobId = _backgroundJobClient.Enqueue<ExtractActionItemsJob>(
                job => job.RunAsync(
                    notification.MeetingId,
                    notification.OrganizationId,
                    notification.PipelineGenerationId,
                    CancellationToken.None));

            if (_postMeetingProcessingTracker is not null)
            {
                await _postMeetingProcessingTracker.MarkStepPendingAsync(
                    notification.OrganizationId,
                    notification.MeetingId,
                    PostMeetingProcessingStepType.ActionExtraction,
                    notification.PipelineGenerationId,
                    message: "Action item extraction job enqueued.",
                    relatedHangfireJobId: jobId,
                    cancellationToken: cancellationToken);
            }
        }
    }
}
