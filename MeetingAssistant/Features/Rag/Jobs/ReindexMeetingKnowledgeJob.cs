using MeetingAssistant.Features.LiveSession.Models.PostProcessing;
using MeetingAssistant.Features.LiveSession.Services;
using MeetingAssistant.Features.LiveSession.Services.PostProcessing;
using MeetingAssistant.Features.Rag.Services;
using MeetingAssistant.Infrastructure.Persistence.DbContext;
using Microsoft.EntityFrameworkCore;

namespace MeetingAssistant.Features.Rag.Jobs
{
    public sealed class ReindexMeetingKnowledgeJob(
        ApplicationDbContext dbContext,
        IReindexMeetingKnowledgeService reindexMeetingKnowledgeService,
        ILogger<ReindexMeetingKnowledgeJob> logger,
        IPostMeetingProcessingTracker? postMeetingProcessingTracker = null)
    {
        private readonly ApplicationDbContext _dbContext = dbContext;
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
                var transcript = await _dbContext.MeetingTranscripts
                    .IgnoreQueryFilters()
                    .AsNoTracking()
                    .FirstOrDefaultAsync(x => x.MeetingId == meetingId && x.OrganizationId == organizationId, cancellationToken);

                if (transcript is null || string.IsNullOrWhiteSpace(transcript.FullText))
                {
                    if (_postMeetingProcessingTracker is not null)
                    {
                        await _postMeetingProcessingTracker.FailStepAsync(
                            organizationId,
                            meetingId,
                            PostMeetingProcessingStepType.KnowledgeIndexing,
                            "transcript_unavailable",
                            "Knowledge indexing skipped because meeting transcript was unavailable.",
                            cancellationToken: cancellationToken);
                    }

                    return;
                }

                if (!MeetingTranscriptCompletenessGuard.IsCompleteForDownstream(transcript))
                {
                    if (_postMeetingProcessingTracker is not null)
                    {
                        await _postMeetingProcessingTracker.SkipStepAsync(
                            organizationId,
                            meetingId,
                            PostMeetingProcessingStepType.KnowledgeIndexing,
                            message: MeetingTranscriptCompletenessGuard.BuildIncompleteMessage(transcript),
                            cancellationToken: cancellationToken);

                        await _postMeetingProcessingTracker.RecordEventAsync(
                            organizationId,
                            meetingId,
                            PostMeetingProcessingEventType.Info,
                            PostMeetingProcessingStepType.KnowledgeIndexing,
                            PostMeetingProcessingStatus.Skipped,
                            message: "Knowledge indexing skipped because meeting transcript is incomplete.",
                            errorCode: MeetingTranscriptCompletenessGuard.IncompleteErrorCode,
                            errorMessage: MeetingTranscriptCompletenessGuard.BuildIncompleteMessage(transcript),
                            cancellationToken: cancellationToken);
                    }

                    return;
                }

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
