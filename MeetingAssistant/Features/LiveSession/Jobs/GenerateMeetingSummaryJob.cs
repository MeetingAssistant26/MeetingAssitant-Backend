using Hangfire;
using MeetingAssistant.Api.Infrastructure.Hangfire;
using MeetingAssistant.Features.LiveSession.Models;
using MeetingAssistant.Features.LiveSession.Models.PostProcessing;
using MeetingAssistant.Features.LiveSession.Services;
using MeetingAssistant.Features.LiveSession.Services.PostProcessing;
using MeetingAssistant.Features.Meetings.Jobs;
using MeetingAssistant.Features.Rag.Jobs;
using MeetingAssistant.Infrastructure.Persistence.DbContext;
using Microsoft.EntityFrameworkCore;

namespace MeetingAssistant.Features.LiveSession.Jobs
{
    public class GenerateMeetingSummaryJob(
        ApplicationDbContext dbContext,
        ISummarizerService summarizerService,
        ILogger<GenerateMeetingSummaryJob> logger,
        IPostMeetingProcessingTracker? postMeetingProcessingTracker = null,
        IHangfireJobContextAccessor? hangfireJobContextAccessor = null,
        IBackgroundJobClient? backgroundJobClient = null)
    {
        private readonly ApplicationDbContext _dbContext = dbContext;
        private readonly ISummarizerService _summarizerService = summarizerService;
        private readonly ILogger<GenerateMeetingSummaryJob> _logger = logger;
        private readonly IPostMeetingProcessingTracker? _postMeetingProcessingTracker = postMeetingProcessingTracker;
        private readonly IHangfireJobContextAccessor? _hangfireJobContextAccessor = hangfireJobContextAccessor;

        private Guid? _pipelineGenerationId;
        private string? _currentHangfireJobId;
        private readonly IBackgroundJobClient? _backgroundJobClient = backgroundJobClient;

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
                    PostMeetingProcessingStepType.SummaryGeneration,
                    _pipelineGenerationId,
                    _currentHangfireJobId,
                    message: "Summary generation started.",
                    cancellationToken: cancellationToken);
            }

            var transcript = await _dbContext.MeetingTranscripts
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(x => x.MeetingId == meetingId && x.OrganizationId == organizationId, cancellationToken);

            if (transcript == null || string.IsNullOrWhiteSpace(transcript.FullText))
            {
                if (_postMeetingProcessingTracker is not null)
                {
                    await _postMeetingProcessingTracker.FailStepAsync(
                    organizationId,
                    meetingId,
                    PostMeetingProcessingStepType.SummaryGeneration,
                    "transcript_unavailable",
                    "Summary generation skipped because meeting transcript was unavailable.",
                    _pipelineGenerationId,
                    _currentHangfireJobId,
                    cancellationToken: cancellationToken);
                }

                _logger.LogInformation(
                    "Summary generation skipped because meeting transcript was unavailable. MeetingId={MeetingId}",
                    meetingId);
                return;
            }

            if (!MeetingTranscriptCompletenessGuard.IsCompleteForDownstream(transcript))
            {
                if (_postMeetingProcessingTracker is not null)
                {
                    await _postMeetingProcessingTracker.SkipStepAsync(
                    organizationId,
                    meetingId,
                    PostMeetingProcessingStepType.SummaryGeneration,
                    _pipelineGenerationId,
                    _currentHangfireJobId,
                    message: MeetingTranscriptCompletenessGuard.BuildIncompleteMessage(transcript),
                        cancellationToken: cancellationToken);

                    await _postMeetingProcessingTracker.RecordEventAsync(
                    organizationId,
                    meetingId,
                    PostMeetingProcessingEventType.Info,
                    _pipelineGenerationId,
                    PostMeetingProcessingStepType.SummaryGeneration,
                        PostMeetingProcessingStatus.Skipped,
                        message: "Summary generation skipped because meeting transcript is incomplete.",
                        errorCode: MeetingTranscriptCompletenessGuard.IncompleteErrorCode,
                        errorMessage: MeetingTranscriptCompletenessGuard.BuildIncompleteMessage(transcript),
                        cancellationToken: cancellationToken);
                }

                _logger.LogInformation(
                    "Summary generation skipped because meeting transcript is incomplete. MeetingId={MeetingId} CompletenessStatus={CompletenessStatus}",
                    meetingId,
                    transcript.CompletenessStatus);
                return;
            }

            SummaryResult summaryResult;
            try
            {
                summaryResult = await _summarizerService.SummarizeAsync(transcript.FullText, cancellationToken);
            }
            catch (Exception ex)
            {
                if (_postMeetingProcessingTracker is not null)
                {
                    await _postMeetingProcessingTracker.FailStepAsync(
                    organizationId,
                    meetingId,
                    PostMeetingProcessingStepType.SummaryGeneration,
                    "summary_failed",
                    ex.GetBaseException().Message,
                    _pipelineGenerationId,
                    _currentHangfireJobId,
                    cancellationToken: cancellationToken);
                }

                throw;
            }

            var summary = await _dbContext.MeetingSummaries
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(x => x.MeetingId == meetingId && x.OrganizationId == organizationId, cancellationToken);

            if (summary == null)
            {
                summary = new MeetingSummary
                {
                    MeetingId = meetingId,
                    OrganizationId = organizationId
                };

                _dbContext.MeetingSummaries.Add(summary);
            }

            summary.SummaryText = summaryResult.SummaryText;
            summary.LlmModel = summaryResult.Model;
            summary.PromptTokens = summaryResult.PromptTokens;
            summary.CompletionTokens = summaryResult.CompletionTokens;
            summary.GeneratedAtUtc = DateTime.UtcNow;

            await _dbContext.SaveChangesAsync(cancellationToken);

            if (_postMeetingProcessingTracker is not null)
            {
                await _postMeetingProcessingTracker.CompleteStepAsync(
                    organizationId,
                    meetingId,
                    PostMeetingProcessingStepType.SummaryGeneration,
                    _pipelineGenerationId,
                    _currentHangfireJobId,
                    message: "Summary generated.",
                    artifact: new PostMeetingArtifactLink("meeting_summary", summary.Id),
                    cancellationToken: cancellationToken);
            }

            var tagSuggestionJobId = _backgroundJobClient?.Enqueue<SuggestMeetingTagsJob>(
                job => job.RunAsync(meetingId, organizationId, _pipelineGenerationId, CancellationToken.None));

            if (tagSuggestionJobId is not null && _postMeetingProcessingTracker is not null)
            {
                await _postMeetingProcessingTracker.MarkStepPendingAsync(
                    organizationId,
                    meetingId,
                    PostMeetingProcessingStepType.TagSuggestion,
                    _pipelineGenerationId,
                    message: "Meeting tag suggestion job enqueued after summary generation.",
                    relatedHangfireJobId: tagSuggestionJobId,
                    cancellationToken: cancellationToken);
            }

            var knowledgeJobId = _backgroundJobClient?.Enqueue<ReindexMeetingKnowledgeJob>(
                job => job.RunAsync(meetingId, organizationId, _pipelineGenerationId, CancellationToken.None));

            if (knowledgeJobId is not null && _postMeetingProcessingTracker is not null)
            {
                await _postMeetingProcessingTracker.MarkStepPendingAsync(
                    organizationId,
                    meetingId,
                    PostMeetingProcessingStepType.KnowledgeIndexing,
                    _pipelineGenerationId,
                    message: "Knowledge indexing job enqueued after summary generation. Unconfirmed tag suggestions remain separate from confirmed meeting tag context.",
                    relatedHangfireJobId: knowledgeJobId,
                    cancellationToken: cancellationToken);
            }

            if (_postMeetingProcessingTracker is not null)
            {
                await _postMeetingProcessingTracker.CompleteRunAsync(
                    organizationId,
                    meetingId,
                    _pipelineGenerationId,
                    _currentHangfireJobId,
                    message: "Core transcript and summary artifacts completed; optional post-processing continues independently.",
                    cancellationToken: cancellationToken);
            }
        }
    }
}
