using Hangfire;
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
        IBackgroundJobClient? backgroundJobClient = null)
    {
        private readonly ApplicationDbContext _dbContext = dbContext;
        private readonly ISummarizerService _summarizerService = summarizerService;
        private readonly ILogger<GenerateMeetingSummaryJob> _logger = logger;
        private readonly IPostMeetingProcessingTracker? _postMeetingProcessingTracker = postMeetingProcessingTracker;
        private readonly IBackgroundJobClient? _backgroundJobClient = backgroundJobClient;

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
                    PostMeetingProcessingStepType.SummaryGeneration,
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
                        cancellationToken: cancellationToken);
                }

                _logger.LogInformation(
                    "Summary generation skipped because meeting transcript was unavailable. MeetingId={MeetingId}",
                    meetingId);
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
                    message: "Summary generated.",
                    artifact: new PostMeetingArtifactLink("meeting_summary", summary.Id),
                    cancellationToken: cancellationToken);
            }

            var tagSuggestionJobId = _backgroundJobClient?.Enqueue<SuggestMeetingTagsJob>(
                job => job.RunAsync(meetingId, organizationId, CancellationToken.None));

            if (tagSuggestionJobId is not null && _postMeetingProcessingTracker is not null)
            {
                await _postMeetingProcessingTracker.MarkStepPendingAsync(
                    organizationId,
                    meetingId,
                    PostMeetingProcessingStepType.TagSuggestion,
                    message: "Meeting tag suggestion job enqueued after summary generation.",
                    relatedHangfireJobId: tagSuggestionJobId,
                    cancellationToken: cancellationToken);
            }

            var knowledgeJobId = _backgroundJobClient?.Enqueue<ReindexMeetingKnowledgeJob>(
                job => job.RunAsync(meetingId, organizationId, CancellationToken.None));

            if (knowledgeJobId is not null && _postMeetingProcessingTracker is not null)
            {
                await _postMeetingProcessingTracker.MarkStepPendingAsync(
                    organizationId,
                    meetingId,
                    PostMeetingProcessingStepType.KnowledgeIndexing,
                    message: "Knowledge indexing job enqueued after summary generation. Unconfirmed tag suggestions remain separate from confirmed meeting tag context.",
                    relatedHangfireJobId: knowledgeJobId,
                    cancellationToken: cancellationToken);
            }

            if (_postMeetingProcessingTracker is not null)
            {
                await _postMeetingProcessingTracker.CompleteRunAsync(
                    organizationId,
                    meetingId,
                    message: "Core transcript and summary artifacts completed; optional post-processing continues independently.",
                    cancellationToken: cancellationToken);
            }
        }
    }
}
