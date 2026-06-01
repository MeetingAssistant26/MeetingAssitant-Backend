using MeetingAssistant.Features.LiveSession.Models.PostProcessing;
using MeetingAssistant.Features.LiveSession.Services.PostProcessing;
using MeetingAssistant.Features.Rag.Services;

namespace MeetingAssistant.Features.Rag.Jobs
{
    public sealed class ReindexMeetingKnowledgeJob(
        IReindexMeetingKnowledgeService reindexMeetingKnowledgeService,
        ILogger<ReindexMeetingKnowledgeJob> logger,
        IPostMeetingProcessingTracker? postMeetingProcessingTracker = null)
    {
        private readonly IReindexMeetingKnowledgeService _reindexMeetingKnowledgeService = reindexMeetingKnowledgeService;
        private readonly ILogger<ReindexMeetingKnowledgeJob> _logger = logger;
        private readonly IPostMeetingProcessingTracker? _postMeetingProcessingTracker = postMeetingProcessingTracker;

        [Hangfire.AutomaticRetry(Attempts = 3)]
        public async Task RunAsync(
            Guid meetingId,
            Guid organizationId,
            CancellationToken cancellationToken = default)
        {
            if (_postMeetingProcessingTracker is not null)
            {
                await _postMeetingProcessingTracker.StartStepAsync(
                    organizationId,
                    meetingId,
                    PostMeetingProcessingStepType.KnowledgeIndexing,
                    message: "Knowledge indexing started.",
                    cancellationToken: cancellationToken);
            }

            try
            {
                var result = await _reindexMeetingKnowledgeService.ReindexMeetingAsync(
                    organizationId,
                    meetingId,
                    cancellationToken);

                if (_postMeetingProcessingTracker is not null)
                {
                    await _postMeetingProcessingTracker.CompleteStepAsync(
                        organizationId,
                        meetingId,
                        PostMeetingProcessingStepType.KnowledgeIndexing,
                        message: $"Published {result.PublishedDocumentCount} knowledge document(s) and {result.PublishedChunkCount} chunk(s).",
                        artifact: new PostMeetingArtifactLink(
                            "knowledge_document",
                            ArtifactIds: result.PublishedDocumentIds),
                        cancellationToken: cancellationToken);
                }
            }
            catch (Exception ex)
            {
                if (_postMeetingProcessingTracker is not null)
                {
                    await _postMeetingProcessingTracker.FailStepAsync(
                        organizationId,
                        meetingId,
                        PostMeetingProcessingStepType.KnowledgeIndexing,
                        "knowledge_indexing_failed",
                        ex.GetBaseException().Message,
                        cancellationToken: cancellationToken);
                }

                _logger.LogError(
                    ex,
                    "Knowledge indexing failed. MeetingId={MeetingId} OrganizationId={OrganizationId}",
                    meetingId,
                    organizationId);
                throw;
            }
        }
    }
}
