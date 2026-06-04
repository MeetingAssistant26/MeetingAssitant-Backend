using MeetingAssistant.Api.Infrastructure.Hangfire;
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
        IPostMeetingProcessingTracker? postMeetingProcessingTracker = null,
        IHangfireJobContextAccessor? hangfireJobContextAccessor = null)
    {
        private readonly ApplicationDbContext _dbContext = dbContext;
        private readonly IReindexMeetingKnowledgeService _reindexMeetingKnowledgeService = reindexMeetingKnowledgeService;
        private readonly ILogger<ReindexMeetingKnowledgeJob> _logger = logger;
        private readonly IPostMeetingProcessingTracker? _postMeetingProcessingTracker = postMeetingProcessingTracker;
        private readonly IHangfireJobContextAccessor? _hangfireJobContextAccessor = hangfireJobContextAccessor;

        private Guid? _pipelineGenerationId;
        private string? _currentHangfireJobId;

        [Hangfire.AutomaticRetry(Attempts = 3)]
        public Task RunAsync(
            Guid meetingId,
            Guid organizationId,
            CancellationToken cancellationToken = default)
            => RunAsync(meetingId, organizationId, pipelineGenerationId: null, cancellationToken);

        public async Task RunAsync(
            Guid meetingId,
            Guid organizationId,
            Guid? pipelineGenerationId,
            CancellationToken cancellationToken = default)
        {
            _currentHangfireJobId = _hangfireJobContextAccessor?.CurrentJobId;
            _pipelineGenerationId = pipelineGenerationId;
            if (_postMeetingProcessingTracker is not null && !_pipelineGenerationId.HasValue)
            {
                var run = await _postMeetingProcessingTracker.EnsureRunAsync(
                    organizationId,
                    meetingId,
                    relatedHangfireJobId: _currentHangfireJobId,
                    cancellationToken: cancellationToken);
                _pipelineGenerationId = run.PipelineGenerationId;
            }

            if (_postMeetingProcessingTracker is not null)
            {
                await _postMeetingProcessingTracker.StartStepAsync(
                    organizationId,
                    meetingId,
                    PostMeetingProcessingStepType.KnowledgeIndexing,
                    _pipelineGenerationId,
                    _currentHangfireJobId,
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
                    _pipelineGenerationId,
                    _currentHangfireJobId,
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
                    _pipelineGenerationId,
                    _currentHangfireJobId,
                    message: MeetingTranscriptCompletenessGuard.BuildIncompleteMessage(transcript),
                            cancellationToken: cancellationToken);

                        await _postMeetingProcessingTracker.RecordEventAsync(
                    organizationId,
                    meetingId,
                    PostMeetingProcessingEventType.Info,
                    _pipelineGenerationId,
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
                    _pipelineGenerationId,
                    _currentHangfireJobId,
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
                    _pipelineGenerationId,
                    _currentHangfireJobId,
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
