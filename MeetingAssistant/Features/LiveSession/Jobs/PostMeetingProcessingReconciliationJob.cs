using MeetingAssistant.Features.LiveSession.Models;
using MeetingAssistant.Features.LiveSession.Models.PostProcessing;
using MeetingAssistant.Features.LiveSession.Services.PostProcessing;
using MeetingAssistant.Features.Rag.Models;
using MeetingAssistant.Infrastructure.Persistence.DbContext;
using Microsoft.EntityFrameworkCore;

namespace MeetingAssistant.Features.LiveSession.Jobs
{
    public sealed class PostMeetingProcessingReconciliationJob(
        ApplicationDbContext dbContext,
        IPostMeetingProcessingTracker postMeetingProcessingTracker,
        ILogger<PostMeetingProcessingReconciliationJob> logger)
    {
        private static readonly TimeSpan StaleRunThreshold = TimeSpan.FromMinutes(60);
        private const int BatchSize = 100;

        private readonly ApplicationDbContext _dbContext = dbContext;
        private readonly IPostMeetingProcessingTracker _postMeetingProcessingTracker = postMeetingProcessingTracker;
        private readonly ILogger<PostMeetingProcessingReconciliationJob> _logger = logger;

        public async Task RunAsync(CancellationToken cancellationToken = default)
        {
            var now = DateTime.UtcNow;
            var cutoffUtc = now.Subtract(StaleRunThreshold);

            var staleRuns = await _dbContext.PostMeetingProcessingRuns
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(x => x.Status == PostMeetingProcessingStatus.Pending
                            || x.Status == PostMeetingProcessingStatus.InProgress)
                .Where(x => (x.StartedAtUtc ?? x.CreatedAtUtc) <= cutoffUtc)
                .OrderBy(x => x.CreatedAtUtc)
                .ThenBy(x => x.Id)
                .Take(BatchSize)
                .ToListAsync(cancellationToken);

            foreach (var run in staleRuns)
            {
                await ReconcileRunAsync(run, cutoffUtc, cancellationToken);
            }
        }

        private async Task ReconcileRunAsync(
            PostMeetingProcessingRun run,
            DateTime cutoffUtc,
            CancellationToken cancellationToken)
        {
            var transcript = await _dbContext.MeetingTranscripts
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(x => x.OrganizationId == run.OrganizationId && x.MeetingId == run.MeetingId)
                .OrderByDescending(x => x.GeneratedAtUtc)
                .FirstOrDefaultAsync(cancellationToken);

            var summary = await _dbContext.MeetingSummaries
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(x => x.OrganizationId == run.OrganizationId && x.MeetingId == run.MeetingId)
                .OrderByDescending(x => x.GeneratedAtUtc)
                .FirstOrDefaultAsync(cancellationToken);

            if (transcript is not null)
            {
                await CompleteStaleStepAsync(
                    run,
                    PostMeetingProcessingStepType.Stt,
                    cutoffUtc,
                    "STT reconciled from existing transcript artifact.",
                    new PostMeetingArtifactLink("meeting_transcript", transcript.Id),
                    cancellationToken);

                await CompleteStaleStepAsync(
                    run,
                    PostMeetingProcessingStepType.TranscriptPersistence,
                    cutoffUtc,
                    "Transcript persistence reconciled from existing transcript artifact.",
                    new PostMeetingArtifactLink("meeting_transcript", transcript.Id),
                    cancellationToken);
            }

            if (summary is not null)
            {
                await CompleteStaleStepAsync(
                    run,
                    PostMeetingProcessingStepType.SummaryGeneration,
                    cutoffUtc,
                    "Summary generation reconciled from existing summary artifact.",
                    new PostMeetingArtifactLink("meeting_summary", summary.Id),
                    cancellationToken);
            }

            await ReconcileStaleOptionalStepsAsync(run, cutoffUtc, cancellationToken);

            if (transcript is not null && summary is not null)
            {
                await _postMeetingProcessingTracker.CompleteRunAsync(
                    run.OrganizationId,
                    run.MeetingId,
                    message: "Stale post-meeting processing run reconciled from durable transcript and summary artifacts.",
                    cancellationToken: cancellationToken);

                _logger.LogInformation(
                    "Reconciled stale post-meeting processing run from durable artifacts. RunId={RunId} MeetingId={MeetingId} OrganizationId={OrganizationId}",
                    run.Id,
                    run.MeetingId,
                    run.OrganizationId);
            }
        }

        private async Task CompleteStaleStepAsync(
            PostMeetingProcessingRun run,
            PostMeetingProcessingStepType stepType,
            DateTime cutoffUtc,
            string message,
            PostMeetingArtifactLink artifact,
            CancellationToken cancellationToken)
        {
            var step = await GetStepAsync(run.Id, stepType, cancellationToken);
            if (step is not null && (IsTerminal(step.Status) || !IsStale(step, cutoffUtc)))
            {
                return;
            }

            await _postMeetingProcessingTracker.CompleteStepAsync(
                run.OrganizationId,
                run.MeetingId,
                stepType,
                message: message,
                artifact: artifact,
                cancellationToken: cancellationToken);
        }

        private async Task ReconcileStaleOptionalStepsAsync(
            PostMeetingProcessingRun run,
            DateTime cutoffUtc,
            CancellationToken cancellationToken)
        {
            var optionalSteps = await _dbContext.PostMeetingProcessingSteps
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(x => x.RunId == run.Id)
                .Where(x => x.StepType == PostMeetingProcessingStepType.ActionExtraction
                            || x.StepType == PostMeetingProcessingStepType.TagSuggestion
                            || x.StepType == PostMeetingProcessingStepType.KnowledgeIndexing
                            || x.StepType == PostMeetingProcessingStepType.ProviderSync)
                .Where(x => x.Status == PostMeetingProcessingStatus.Pending
                            || x.Status == PostMeetingProcessingStatus.InProgress)
                .ToListAsync(cancellationToken);

            foreach (var step in optionalSteps.Where(x => IsStale(x, cutoffUtc)))
            {
                var artifact = await ResolveOptionalArtifactAsync(
                    run.OrganizationId,
                    run.MeetingId,
                    step.StepType,
                    cancellationToken);

                if (artifact is null)
                {
                    await _postMeetingProcessingTracker.SkipStepAsync(
                        run.OrganizationId,
                        run.MeetingId,
                        step.StepType,
                        message: "Stale optional post-processing step skipped during reconciliation because no durable artifact exists.",
                        cancellationToken: cancellationToken);
                }
                else
                {
                    await _postMeetingProcessingTracker.CompleteStepAsync(
                        run.OrganizationId,
                        run.MeetingId,
                        step.StepType,
                        message: "Stale optional post-processing step reconciled from durable artifacts.",
                        artifact: artifact,
                        cancellationToken: cancellationToken);
                }
            }
        }

        private async Task<PostMeetingArtifactLink?> ResolveOptionalArtifactAsync(
            Guid organizationId,
            Guid meetingId,
            PostMeetingProcessingStepType stepType,
            CancellationToken cancellationToken)
        {
            return stepType switch
            {
                PostMeetingProcessingStepType.ActionExtraction => await ResolveActionItemsArtifactAsync(organizationId, meetingId, cancellationToken),
                PostMeetingProcessingStepType.TagSuggestion => await ResolveTagSuggestionsArtifactAsync(organizationId, meetingId, cancellationToken),
                PostMeetingProcessingStepType.KnowledgeIndexing => await ResolveKnowledgeArtifactAsync(organizationId, meetingId, cancellationToken),
                PostMeetingProcessingStepType.ProviderSync => await ResolveProviderSyncArtifactAsync(organizationId, meetingId, cancellationToken),
                _ => null
            };
        }

        private async Task<PostMeetingArtifactLink?> ResolveActionItemsArtifactAsync(
            Guid organizationId,
            Guid meetingId,
            CancellationToken cancellationToken)
        {
            var ids = await _dbContext.ActionItems
                .IgnoreQueryFilters()
                .Where(x => x.OrganizationId == organizationId && x.MeetingId == meetingId)
                .OrderBy(x => x.Id)
                .Select(x => x.Id)
                .ToListAsync(cancellationToken);

            return ids.Count == 0 ? null : new PostMeetingArtifactLink("action_item", ArtifactIds: ids);
        }

        private async Task<PostMeetingArtifactLink?> ResolveTagSuggestionsArtifactAsync(
            Guid organizationId,
            Guid meetingId,
            CancellationToken cancellationToken)
        {
            var ids = await _dbContext.MeetingTagSuggestions
                .IgnoreQueryFilters()
                .Where(x => x.OrganizationId == organizationId && x.MeetingId == meetingId)
                .OrderBy(x => x.Id)
                .Select(x => x.Id)
                .ToListAsync(cancellationToken);

            return ids.Count == 0 ? null : new PostMeetingArtifactLink("meeting_tag_suggestion", ArtifactIds: ids);
        }

        private async Task<PostMeetingArtifactLink?> ResolveKnowledgeArtifactAsync(
            Guid organizationId,
            Guid meetingId,
            CancellationToken cancellationToken)
        {
            var ids = await _dbContext.KnowledgeDocuments
                .IgnoreQueryFilters()
                .Where(x => x.OrganizationId == organizationId
                            && x.MeetingId == meetingId
                            && x.Visibility == KnowledgeVisibility.Published
                            && x.IsCurrent)
                .OrderBy(x => x.Id)
                .Select(x => x.Id)
                .ToListAsync(cancellationToken);

            return ids.Count == 0 ? null : new PostMeetingArtifactLink("knowledge_document", ArtifactIds: ids);
        }

        private async Task<PostMeetingArtifactLink?> ResolveProviderSyncArtifactAsync(
            Guid organizationId,
            Guid meetingId,
            CancellationToken cancellationToken)
        {
            var ids = await _dbContext.ActionItems
                .IgnoreQueryFilters()
                .Where(x => x.OrganizationId == organizationId
                            && x.MeetingId == meetingId
                            && x.ExternalTaskId != null)
                .OrderBy(x => x.Id)
                .Select(x => x.Id)
                .ToListAsync(cancellationToken);

            return ids.Count == 0 ? null : new PostMeetingArtifactLink("action_item", ArtifactIds: ids);
        }

        private async Task<PostMeetingProcessingStep?> GetStepAsync(
            Guid runId,
            PostMeetingProcessingStepType stepType,
            CancellationToken cancellationToken)
        {
            return await _dbContext.PostMeetingProcessingSteps
                .IgnoreQueryFilters()
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.RunId == runId && x.StepType == stepType, cancellationToken);
        }

        private static bool IsStale(PostMeetingProcessingStep step, DateTime cutoffUtc)
        {
            var lastActivityUtc = step.LastAttemptAtUtc
                                  ?? step.StartedAtUtc
                                  ?? step.CreatedAtUtc;

            return lastActivityUtc <= cutoffUtc;
        }

        private static bool IsTerminal(PostMeetingProcessingStatus status)
        {
            return status is PostMeetingProcessingStatus.Completed
                or PostMeetingProcessingStatus.Failed
                or PostMeetingProcessingStatus.Skipped;
        }
    }
}
