using Hangfire;
using MediatR;
using MeetingAssistant.Features.LiveSession.Models.Events;
using MeetingAssistant.Features.LiveSession.Models.PostProcessing;
using MeetingAssistant.Features.LiveSession.Services.PostProcessing;
using MeetingAssistant.Features.Rag.Jobs;

namespace MeetingAssistant.Features.Rag.Handlers
{
    public sealed class EnqueueKnowledgeReindexOnTranscriptReadyHandler(
        IBackgroundJobClient backgroundJobClient,
        IPostMeetingProcessingTracker? postMeetingProcessingTracker = null) : INotificationHandler<MeetingTranscriptReadyEvent>
    {
        private readonly IBackgroundJobClient _backgroundJobClient = backgroundJobClient;
        private readonly IPostMeetingProcessingTracker? _postMeetingProcessingTracker = postMeetingProcessingTracker;

        public async Task Handle(MeetingTranscriptReadyEvent notification, CancellationToken cancellationToken)
        {
            var jobId = _backgroundJobClient.Enqueue<ReindexMeetingKnowledgeJob>(
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
                    PostMeetingProcessingStepType.KnowledgeIndexing,
                    notification.PipelineGenerationId,
                    message: "Knowledge indexing job enqueued after transcript persistence.",
                    relatedHangfireJobId: jobId,
                    cancellationToken: cancellationToken);
            }
        }
    }
}
