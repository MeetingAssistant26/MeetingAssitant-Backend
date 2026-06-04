using System.Text.Json;
using MeetingAssistant.Features.LiveSession.Models.PostProcessing;
using MeetingAssistant.Infrastructure.Persistence.DbContext;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace MeetingAssistant.Features.LiveSession.Services.PostProcessing
{
    public sealed class PostMeetingProcessingTracker(ApplicationDbContext dbContext) : IPostMeetingProcessingTracker
    {
        private const int MaxErrorMessageLength = 2000;
        private const int MaxMessageLength = 2000;
        private const string UniqueViolationSqlState = "23505";
        private const int SqliteConstraintViolationErrorCode = 19;
        private const string StepRunStepTypeUniqueIndex = "UX_PostMeetingProcessingSteps_Run_StepType";
        private const string RunOrgMeetingGenerationUniqueIndex = "UX_PostMeetingProcessingRuns_Org_Meeting_Generation";

        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

        private readonly ApplicationDbContext _dbContext = dbContext;

        public Task<PostMeetingProcessingRun> EnsureRunAsync(
            Guid organizationId,
            Guid meetingId,
            Guid? pipelineGenerationId = null,
            string? relatedHangfireJobId = null,
            CancellationToken cancellationToken = default)
            => ExecuteWithDuplicateRecoveryAsync(
                ct => EnsureRunInternalAsync(
                    organizationId,
                    meetingId,
                    pipelineGenerationId,
                    relatedHangfireJobId,
                    ct),
                cancellationToken);

        private async Task<PostMeetingProcessingRun> EnsureRunInternalAsync(
            Guid organizationId,
            Guid meetingId,
            Guid? pipelineGenerationId,
            string? relatedHangfireJobId,
            CancellationToken cancellationToken)
        {
            var run = pipelineGenerationId.HasValue
                ? await _dbContext.PostMeetingProcessingRuns
                    .IgnoreQueryFilters()
                    .FirstOrDefaultAsync(
                        x => x.OrganizationId == organizationId
                             && x.MeetingId == meetingId
                             && x.PipelineGenerationId == pipelineGenerationId.Value,
                        cancellationToken)
                : await _dbContext.PostMeetingProcessingRuns
                    .IgnoreQueryFilters()
                    .Where(x => x.OrganizationId == organizationId && x.MeetingId == meetingId)
                    .OrderByDescending(x => x.CreatedAtUtc)
                    .ThenByDescending(x => x.Id)
                    .FirstOrDefaultAsync(cancellationToken);

            if (run is not null)
            {
                if (!string.IsNullOrWhiteSpace(relatedHangfireJobId) && run.RelatedHangfireJobId != relatedHangfireJobId)
                {
                    run.RelatedHangfireJobId = relatedHangfireJobId;
                    await _dbContext.SaveChangesAsync(cancellationToken);
                }

                return run;
            }

            var now = DateTime.UtcNow;
            run = new PostMeetingProcessingRun
            {
                OrganizationId = organizationId,
                MeetingId = meetingId,
                PipelineGenerationId = pipelineGenerationId ?? Guid.NewGuid(),
                Status = PostMeetingProcessingStatus.Pending,
                RelatedHangfireJobId = relatedHangfireJobId
            };

            _dbContext.PostMeetingProcessingRuns.Add(run);
            _dbContext.PostMeetingProcessingEvents.Add(new PostMeetingProcessingEvent
            {
                OrganizationId = organizationId,
                MeetingId = meetingId,
                RunId = run.Id,
                EventType = PostMeetingProcessingEventType.RunCreated,
                Status = run.Status,
                RelatedHangfireJobId = relatedHangfireJobId,
                OccurredAtUtc = now,
                Message = "Post-meeting processing run created."
            });

            await _dbContext.SaveChangesAsync(cancellationToken);
            return run;
        }

        public Task<PostMeetingProcessingStep> MarkStepPendingAsync(
            Guid organizationId,
            Guid meetingId,
            PostMeetingProcessingStepType stepType,
            Guid? pipelineGenerationId = null,
            string? message = null,
            string? relatedHangfireJobId = null,
            PostMeetingArtifactLink? artifact = null,
            CancellationToken cancellationToken = default)
            => ExecuteWithDuplicateRecoveryAsync(
                ct => MarkStepPendingInternalAsync(
                    organizationId,
                    meetingId,
                    stepType,
                    pipelineGenerationId,
                    message,
                    relatedHangfireJobId,
                    artifact,
                    ct),
                cancellationToken);

        private async Task<PostMeetingProcessingStep> MarkStepPendingInternalAsync(
            Guid organizationId,
            Guid meetingId,
            PostMeetingProcessingStepType stepType,
            Guid? pipelineGenerationId,
            string? message,
            string? relatedHangfireJobId,
            PostMeetingArtifactLink? artifact,
            CancellationToken cancellationToken)
        {
            var run = await EnsureRunAsync(
                organizationId,
                meetingId,
                pipelineGenerationId,
                relatedHangfireJobId,
                cancellationToken);
            var step = await GetOrCreateStepAsync(run, stepType, cancellationToken);

            if (step.Status == PostMeetingProcessingStatus.Pending
                && SameJob(step.RelatedHangfireJobId, relatedHangfireJobId)
                && ArtifactMatches(step, artifact))
            {
                return step;
            }

            step.Status = PostMeetingProcessingStatus.Pending;
            step.RelatedHangfireJobId = relatedHangfireJobId ?? step.RelatedHangfireJobId;
            step.CompletedAtUtc = null;
            step.FailedAtUtc = null;
            step.ErrorCode = null;
            step.ErrorMessage = null;
            ApplyArtifact(step, artifact);

            AddStepEvent(run, step, PostMeetingProcessingEventType.StepPending, message, relatedHangfireJobId, artifact);
            await _dbContext.SaveChangesAsync(cancellationToken);
            return step;
        }

        public Task<PostMeetingProcessingStep> StartStepAsync(
            Guid organizationId,
            Guid meetingId,
            PostMeetingProcessingStepType stepType,
            Guid? pipelineGenerationId = null,
            string? relatedHangfireJobId = null,
            string? message = null,
            PostMeetingArtifactLink? artifact = null,
            CancellationToken cancellationToken = default)
            => ExecuteWithDuplicateRecoveryAsync(
                ct => StartStepInternalAsync(
                    organizationId,
                    meetingId,
                    stepType,
                    pipelineGenerationId,
                    relatedHangfireJobId,
                    message,
                    artifact,
                    ct),
                cancellationToken);

        private async Task<PostMeetingProcessingStep> StartStepInternalAsync(
            Guid organizationId,
            Guid meetingId,
            PostMeetingProcessingStepType stepType,
            Guid? pipelineGenerationId,
            string? relatedHangfireJobId,
            string? message,
            PostMeetingArtifactLink? artifact,
            CancellationToken cancellationToken)
        {
            var run = await EnsureRunAsync(
                organizationId,
                meetingId,
                pipelineGenerationId,
                relatedHangfireJobId,
                cancellationToken);
            var step = await GetOrCreateStepAsync(run, stepType, cancellationToken);

            if (step.Status == PostMeetingProcessingStatus.Completed
                && SameJob(step.RelatedHangfireJobId, relatedHangfireJobId))
            {
                return step;
            }

            var sameInProgressAttempt = step.Status == PostMeetingProcessingStatus.InProgress
                && SameJob(step.RelatedHangfireJobId, relatedHangfireJobId);
            if (sameInProgressAttempt)
            {
                return step;
            }

            var now = DateTime.UtcNow;
            var isRetry = step.Status == PostMeetingProcessingStatus.Failed
                || step.Status == PostMeetingProcessingStatus.Completed
                || step.Status == PostMeetingProcessingStatus.CompletedWithWarnings
                || step.AttemptCount > 0;

            step.Status = PostMeetingProcessingStatus.InProgress;
            step.AttemptCount += 1;
            step.LastAttemptAtUtc = now;
            step.StartedAtUtc ??= now;
            step.CompletedAtUtc = null;
            step.FailedAtUtc = null;
            step.RelatedHangfireJobId = relatedHangfireJobId ?? step.RelatedHangfireJobId;
            step.ErrorCode = null;
            step.ErrorMessage = null;
            ApplyArtifact(step, artifact);

            var keepCompletedRunClosed = run.Status == PostMeetingProcessingStatus.Completed && IsOptionalStep(stepType);
            if (!keepCompletedRunClosed)
            {
                run.Status = PostMeetingProcessingStatus.InProgress;
                run.StartedAtUtc ??= now;
                if (run.AttemptCount == 0)
                {
                    run.AttemptCount = 1;
                }
                else if (run.FailedAtUtc.HasValue)
                {
                    run.AttemptCount += 1;
                }

                run.FailedAtUtc = null;
                run.ErrorCode = null;
                run.ErrorMessage = null;
                AddRunStatusEvent(run, PostMeetingProcessingEventType.RunStatusChanged, "Post-meeting processing is in progress.", relatedHangfireJobId);
            }

            if (!string.IsNullOrWhiteSpace(relatedHangfireJobId))
            {
                run.RelatedHangfireJobId = relatedHangfireJobId;
            }

            AddStepEvent(
                run,
                step,
                isRetry ? PostMeetingProcessingEventType.StepRetried : PostMeetingProcessingEventType.StepStarted,
                message,
                relatedHangfireJobId,
                artifact);

            await _dbContext.SaveChangesAsync(cancellationToken);
            return step;
        }

        public Task<PostMeetingProcessingStep> CompleteStepAsync(
            Guid organizationId,
            Guid meetingId,
            PostMeetingProcessingStepType stepType,
            Guid? pipelineGenerationId = null,
            string? relatedHangfireJobId = null,
            string? message = null,
            PostMeetingArtifactLink? artifact = null,
            CancellationToken cancellationToken = default)
            => ExecuteWithDuplicateRecoveryAsync(
                ct => CompleteStepInternalAsync(
                    organizationId,
                    meetingId,
                    stepType,
                    pipelineGenerationId,
                    relatedHangfireJobId,
                    message,
                    artifact,
                    ct),
                cancellationToken);

        private async Task<PostMeetingProcessingStep> CompleteStepInternalAsync(
            Guid organizationId,
            Guid meetingId,
            PostMeetingProcessingStepType stepType,
            Guid? pipelineGenerationId,
            string? relatedHangfireJobId,
            string? message,
            PostMeetingArtifactLink? artifact,
            CancellationToken cancellationToken)
        {
            var run = await EnsureRunAsync(
                organizationId,
                meetingId,
                pipelineGenerationId,
                relatedHangfireJobId,
                cancellationToken);
            var step = await GetOrCreateStepAsync(run, stepType, cancellationToken);

            if (step.Status == PostMeetingProcessingStatus.Completed
                && SameJob(step.RelatedHangfireJobId, relatedHangfireJobId)
                && ArtifactMatches(step, artifact))
            {
                return step;
            }

            var now = DateTime.UtcNow;
            step.Status = PostMeetingProcessingStatus.Completed;
            step.StartedAtUtc ??= now;
            step.CompletedAtUtc = now;
            step.FailedAtUtc = null;
            step.RelatedHangfireJobId = relatedHangfireJobId ?? step.RelatedHangfireJobId;
            step.ErrorCode = null;
            step.ErrorMessage = null;
            ApplyArtifact(step, artifact);

            run.Status = run.Status == PostMeetingProcessingStatus.Pending
                ? PostMeetingProcessingStatus.InProgress
                : run.Status;
            run.StartedAtUtc ??= now;
            if (!string.IsNullOrWhiteSpace(relatedHangfireJobId))
            {
                run.RelatedHangfireJobId = relatedHangfireJobId;
            }

            AddStepEvent(run, step, PostMeetingProcessingEventType.StepCompleted, message, relatedHangfireJobId, artifact);
            if (artifact is { ArtifactId: not null } || artifact?.ArtifactIds?.Count > 0)
            {
                AddStepEvent(run, step, PostMeetingProcessingEventType.ArtifactLinked, "Step artifacts linked.", relatedHangfireJobId, artifact);
            }

            await _dbContext.SaveChangesAsync(cancellationToken);
            return step;
        }

        public Task<PostMeetingProcessingStep> CompleteStepWithWarningsAsync(
            Guid organizationId,
            Guid meetingId,
            PostMeetingProcessingStepType stepType,
            Guid? pipelineGenerationId = null,
            string? relatedHangfireJobId = null,
            string? message = null,
            PostMeetingArtifactLink? artifact = null,
            CancellationToken cancellationToken = default)
            => ExecuteWithDuplicateRecoveryAsync(
                ct => CompleteStepWithWarningsInternalAsync(
                    organizationId,
                    meetingId,
                    stepType,
                    pipelineGenerationId,
                    relatedHangfireJobId,
                    message,
                    artifact,
                    ct),
                cancellationToken);

        private async Task<PostMeetingProcessingStep> CompleteStepWithWarningsInternalAsync(
            Guid organizationId,
            Guid meetingId,
            PostMeetingProcessingStepType stepType,
            Guid? pipelineGenerationId,
            string? relatedHangfireJobId,
            string? message,
            PostMeetingArtifactLink? artifact,
            CancellationToken cancellationToken)
        {
            var run = await EnsureRunAsync(
                organizationId,
                meetingId,
                pipelineGenerationId,
                relatedHangfireJobId,
                cancellationToken);
            var step = await GetOrCreateStepAsync(run, stepType, cancellationToken);

            if (step.Status == PostMeetingProcessingStatus.CompletedWithWarnings
                && SameJob(step.RelatedHangfireJobId, relatedHangfireJobId)
                && ArtifactMatches(step, artifact))
            {
                return step;
            }

            var now = DateTime.UtcNow;
            step.Status = PostMeetingProcessingStatus.CompletedWithWarnings;
            step.StartedAtUtc ??= now;
            step.CompletedAtUtc = now;
            step.FailedAtUtc = null;
            step.RelatedHangfireJobId = relatedHangfireJobId ?? step.RelatedHangfireJobId;
            step.ErrorCode = null;
            step.ErrorMessage = null;
            ApplyArtifact(step, artifact);

            run.Status = run.Status == PostMeetingProcessingStatus.Pending
                ? PostMeetingProcessingStatus.InProgress
                : run.Status;
            run.StartedAtUtc ??= now;
            if (!string.IsNullOrWhiteSpace(relatedHangfireJobId))
            {
                run.RelatedHangfireJobId = relatedHangfireJobId;
            }

            AddStepEvent(run, step, PostMeetingProcessingEventType.StepCompleted, message, relatedHangfireJobId, artifact);
            if (artifact is { ArtifactId: not null } || artifact?.ArtifactIds?.Count > 0)
            {
                AddStepEvent(run, step, PostMeetingProcessingEventType.ArtifactLinked, "Step artifacts linked.", relatedHangfireJobId, artifact);
            }

            await _dbContext.SaveChangesAsync(cancellationToken);
            return step;
        }

        public Task<PostMeetingProcessingStep> FailStepAsync(
            Guid organizationId,
            Guid meetingId,
            PostMeetingProcessingStepType stepType,
            string errorCode,
            string errorMessage,
            Guid? pipelineGenerationId = null,
            string? relatedHangfireJobId = null,
            string? message = null,
            PostMeetingArtifactLink? artifact = null,
            CancellationToken cancellationToken = default)
            => ExecuteWithDuplicateRecoveryAsync(
                ct => FailStepInternalAsync(
                    organizationId,
                    meetingId,
                    stepType,
                    errorCode,
                    errorMessage,
                    pipelineGenerationId,
                    relatedHangfireJobId,
                    message,
                    artifact,
                    ct),
                cancellationToken);

        private async Task<PostMeetingProcessingStep> FailStepInternalAsync(
            Guid organizationId,
            Guid meetingId,
            PostMeetingProcessingStepType stepType,
            string errorCode,
            string errorMessage,
            Guid? pipelineGenerationId,
            string? relatedHangfireJobId,
            string? message,
            PostMeetingArtifactLink? artifact,
            CancellationToken cancellationToken)
        {
            var run = await EnsureRunAsync(
                organizationId,
                meetingId,
                pipelineGenerationId,
                relatedHangfireJobId,
                cancellationToken);
            var step = await GetOrCreateStepAsync(run, stepType, cancellationToken);
            var truncatedError = Truncate(errorMessage, MaxErrorMessageLength);

            if (step.Status == PostMeetingProcessingStatus.Failed
                && step.ErrorCode == errorCode
                && step.ErrorMessage == truncatedError
                && SameJob(step.RelatedHangfireJobId, relatedHangfireJobId)
                && ArtifactMatches(step, artifact))
            {
                return step;
            }

            var now = DateTime.UtcNow;
            if (step.AttemptCount == 0)
            {
                step.AttemptCount = 1;
                step.LastAttemptAtUtc = now;
                step.StartedAtUtc ??= now;
            }

            step.Status = PostMeetingProcessingStatus.Failed;
            step.FailedAtUtc = now;
            step.CompletedAtUtc = null;
            step.RelatedHangfireJobId = relatedHangfireJobId ?? step.RelatedHangfireJobId;
            step.ErrorCode = errorCode;
            step.ErrorMessage = truncatedError;
            ApplyArtifact(step, artifact);

            if (!IsOptionalStep(stepType))
            {
                run.Status = PostMeetingProcessingStatus.Failed;
                run.StartedAtUtc ??= now;
                run.FailedAtUtc = now;
                if (!string.IsNullOrWhiteSpace(relatedHangfireJobId))
                {
                    run.RelatedHangfireJobId = relatedHangfireJobId;
                }

                run.ErrorCode = errorCode;
                run.ErrorMessage = truncatedError;

                AddRunStatusEvent(run, PostMeetingProcessingEventType.RunStatusChanged, "Post-meeting processing failed.", relatedHangfireJobId, errorCode, truncatedError);
            }
            else
            {
                run.StartedAtUtc ??= now;
                if (!string.IsNullOrWhiteSpace(relatedHangfireJobId))
                {
                    run.RelatedHangfireJobId = relatedHangfireJobId;
                }
            }

            AddStepEvent(run, step, PostMeetingProcessingEventType.StepFailed, message, relatedHangfireJobId, artifact, errorCode, truncatedError);

            await _dbContext.SaveChangesAsync(cancellationToken);
            return step;
        }

        public Task<PostMeetingProcessingStep> SkipStepAsync(
            Guid organizationId,
            Guid meetingId,
            PostMeetingProcessingStepType stepType,
            Guid? pipelineGenerationId = null,
            string? relatedHangfireJobId = null,
            string? message = null,
            PostMeetingArtifactLink? artifact = null,
            CancellationToken cancellationToken = default)
            => ExecuteWithDuplicateRecoveryAsync(
                ct => SkipStepInternalAsync(
                    organizationId,
                    meetingId,
                    stepType,
                    pipelineGenerationId,
                    relatedHangfireJobId,
                    message,
                    artifact,
                    ct),
                cancellationToken);

        private async Task<PostMeetingProcessingStep> SkipStepInternalAsync(
            Guid organizationId,
            Guid meetingId,
            PostMeetingProcessingStepType stepType,
            Guid? pipelineGenerationId,
            string? relatedHangfireJobId,
            string? message,
            PostMeetingArtifactLink? artifact,
            CancellationToken cancellationToken)
        {
            var run = await EnsureRunAsync(
                organizationId,
                meetingId,
                pipelineGenerationId,
                relatedHangfireJobId,
                cancellationToken);
            var step = await GetOrCreateStepAsync(run, stepType, cancellationToken);

            if (step.Status == PostMeetingProcessingStatus.Skipped
                && SameJob(step.RelatedHangfireJobId, relatedHangfireJobId)
                && ArtifactMatches(step, artifact))
            {
                return step;
            }

            var now = DateTime.UtcNow;
            step.Status = PostMeetingProcessingStatus.Skipped;
            step.StartedAtUtc ??= now;
            step.CompletedAtUtc = now;
            step.FailedAtUtc = null;
            step.RelatedHangfireJobId = relatedHangfireJobId ?? step.RelatedHangfireJobId;
            step.ErrorCode = null;
            step.ErrorMessage = null;
            ApplyArtifact(step, artifact);

            run.Status = run.Status == PostMeetingProcessingStatus.Pending
                ? PostMeetingProcessingStatus.InProgress
                : run.Status;
            run.StartedAtUtc ??= now;
            if (!string.IsNullOrWhiteSpace(relatedHangfireJobId))
            {
                run.RelatedHangfireJobId = relatedHangfireJobId;
            }

            AddStepEvent(run, step, PostMeetingProcessingEventType.StepSkipped, message, relatedHangfireJobId, artifact);
            await _dbContext.SaveChangesAsync(cancellationToken);
            return step;
        }

        public Task<PostMeetingProcessingEvent> RecordEventAsync(
            Guid organizationId,
            Guid meetingId,
            PostMeetingProcessingEventType eventType,
            Guid? pipelineGenerationId = null,
            PostMeetingProcessingStepType? stepType = null,
            PostMeetingProcessingStatus? status = null,
            string? message = null,
            string? relatedHangfireJobId = null,
            PostMeetingArtifactLink? artifact = null,
            string? errorCode = null,
            string? errorMessage = null,
            string? metadataJson = null,
            CancellationToken cancellationToken = default)
            => ExecuteWithDuplicateRecoveryAsync(
                ct => RecordEventInternalAsync(
                    organizationId,
                    meetingId,
                    eventType,
                    pipelineGenerationId,
                    stepType,
                    status,
                    message,
                    relatedHangfireJobId,
                    artifact,
                    errorCode,
                    errorMessage,
                    metadataJson,
                    ct),
                cancellationToken);

        private async Task<PostMeetingProcessingEvent> RecordEventInternalAsync(
            Guid organizationId,
            Guid meetingId,
            PostMeetingProcessingEventType eventType,
            Guid? pipelineGenerationId,
            PostMeetingProcessingStepType? stepType,
            PostMeetingProcessingStatus? status,
            string? message,
            string? relatedHangfireJobId,
            PostMeetingArtifactLink? artifact,
            string? errorCode,
            string? errorMessage,
            string? metadataJson,
            CancellationToken cancellationToken)
        {
            var run = await EnsureRunAsync(
                organizationId,
                meetingId,
                pipelineGenerationId,
                relatedHangfireJobId,
                cancellationToken);
            PostMeetingProcessingStep? step = null;
            if (stepType.HasValue)
            {
                step = await GetOrCreateStepAsync(run, stepType.Value, cancellationToken);
            }

            var processingEvent = BuildEvent(
                run,
                step,
                eventType,
                status ?? step?.Status ?? run.Status,
                message,
                relatedHangfireJobId,
                artifact,
                errorCode,
                errorMessage,
                metadataJson);

            _dbContext.PostMeetingProcessingEvents.Add(processingEvent);
            await _dbContext.SaveChangesAsync(cancellationToken);
            return processingEvent;
        }

        public Task<PostMeetingProcessingRun> CompleteRunAsync(
            Guid organizationId,
            Guid meetingId,
            Guid? pipelineGenerationId = null,
            string? relatedHangfireJobId = null,
            string? message = null,
            CancellationToken cancellationToken = default)
            => ExecuteWithDuplicateRecoveryAsync(
                ct => CompleteRunInternalAsync(
                    organizationId,
                    meetingId,
                    pipelineGenerationId,
                    relatedHangfireJobId,
                    message,
                    ct),
                cancellationToken);

        private async Task<PostMeetingProcessingRun> CompleteRunInternalAsync(
            Guid organizationId,
            Guid meetingId,
            Guid? pipelineGenerationId,
            string? relatedHangfireJobId,
            string? message,
            CancellationToken cancellationToken)
        {
            var run = await EnsureRunAsync(
                organizationId,
                meetingId,
                pipelineGenerationId,
                relatedHangfireJobId,
                cancellationToken);

            if (run.Status == PostMeetingProcessingStatus.Completed && SameJob(run.RelatedHangfireJobId, relatedHangfireJobId))
            {
                return run;
            }

            var now = DateTime.UtcNow;
            run.Status = PostMeetingProcessingStatus.Completed;
            run.StartedAtUtc ??= now;
            run.CompletedAtUtc = now;
            run.FailedAtUtc = null;
            if (!string.IsNullOrWhiteSpace(relatedHangfireJobId))
            {
                run.RelatedHangfireJobId = relatedHangfireJobId;
            }

            run.ErrorCode = null;
            run.ErrorMessage = null;

            AddRunStatusEvent(
                run,
                PostMeetingProcessingEventType.RunStatusChanged,
                message ?? "Post-meeting processing completed.",
                relatedHangfireJobId);

            await _dbContext.SaveChangesAsync(cancellationToken);
            return run;
        }

        public Task<PostMeetingProcessingRun> CompleteRunWithWarningsAsync(
            Guid organizationId,
            Guid meetingId,
            Guid? pipelineGenerationId = null,
            string? relatedHangfireJobId = null,
            string? message = null,
            CancellationToken cancellationToken = default)
            => ExecuteWithDuplicateRecoveryAsync(
                ct => CompleteRunWithWarningsInternalAsync(
                    organizationId,
                    meetingId,
                    pipelineGenerationId,
                    relatedHangfireJobId,
                    message,
                    ct),
                cancellationToken);

        private async Task<PostMeetingProcessingRun> CompleteRunWithWarningsInternalAsync(
            Guid organizationId,
            Guid meetingId,
            Guid? pipelineGenerationId,
            string? relatedHangfireJobId,
            string? message,
            CancellationToken cancellationToken)
        {
            var run = await EnsureRunAsync(
                organizationId,
                meetingId,
                pipelineGenerationId,
                relatedHangfireJobId,
                cancellationToken);

            if (run.Status == PostMeetingProcessingStatus.CompletedWithWarnings && SameJob(run.RelatedHangfireJobId, relatedHangfireJobId))
            {
                return run;
            }

            var now = DateTime.UtcNow;
            run.Status = PostMeetingProcessingStatus.CompletedWithWarnings;
            run.StartedAtUtc ??= now;
            run.CompletedAtUtc = now;
            run.FailedAtUtc = null;
            if (!string.IsNullOrWhiteSpace(relatedHangfireJobId))
            {
                run.RelatedHangfireJobId = relatedHangfireJobId;
            }

            run.ErrorCode = null;
            run.ErrorMessage = null;

            AddRunStatusEvent(
                run,
                PostMeetingProcessingEventType.RunStatusChanged,
                message ?? "Post-meeting processing completed with warnings.",
                relatedHangfireJobId);

            await _dbContext.SaveChangesAsync(cancellationToken);
            return run;
        }

        public async Task<PostMeetingProcessingSnapshot> GetLatestByMeetingAsync(
            Guid organizationId,
            Guid meetingId,
            CancellationToken cancellationToken = default)
        {
            var run = await _dbContext.PostMeetingProcessingRuns
                .AsNoTracking()
                .IgnoreQueryFilters()
                .Where(x => x.OrganizationId == organizationId && x.MeetingId == meetingId)
                .OrderByDescending(x => x.CreatedAtUtc)
                .ThenByDescending(x => x.Id)
                .FirstOrDefaultAsync(cancellationToken);

            if (run is null)
            {
                return new PostMeetingProcessingSnapshot(null, Array.Empty<PostMeetingProcessingStep>(), Array.Empty<PostMeetingProcessingEvent>());
            }

            var steps = await _dbContext.PostMeetingProcessingSteps
                .AsNoTracking()
                .IgnoreQueryFilters()
                .Where(x => x.RunId == run.Id)
                .OrderBy(x => x.StepType)
                .ThenBy(x => x.CreatedAtUtc)
                .ToListAsync(cancellationToken);

            var events = await _dbContext.PostMeetingProcessingEvents
                .AsNoTracking()
                .IgnoreQueryFilters()
                .Where(x => x.RunId == run.Id)
                .OrderBy(x => x.OccurredAtUtc)
                .ThenBy(x => x.CreatedAtUtc)
                .ToListAsync(cancellationToken);

            return new PostMeetingProcessingSnapshot(run, steps, events);
        }

        private async Task<PostMeetingProcessingStep> GetOrCreateStepAsync(
            PostMeetingProcessingRun run,
            PostMeetingProcessingStepType stepType,
            CancellationToken cancellationToken)
        {
            var trackedStep = _dbContext.PostMeetingProcessingSteps.Local
                .FirstOrDefault(x => x.RunId == run.Id && x.StepType == stepType);
            if (trackedStep is not null)
            {
                return trackedStep;
            }

            var step = await _dbContext.PostMeetingProcessingSteps
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(x => x.RunId == run.Id && x.StepType == stepType, cancellationToken);

            if (step is not null)
            {
                return step;
            }

            step = new PostMeetingProcessingStep
            {
                OrganizationId = run.OrganizationId,
                MeetingId = run.MeetingId,
                RunId = run.Id,
                StepType = stepType,
                Status = PostMeetingProcessingStatus.Pending
            };

            _dbContext.PostMeetingProcessingSteps.Add(step);
            return step;
        }

        private void AddStepEvent(
            PostMeetingProcessingRun run,
            PostMeetingProcessingStep step,
            PostMeetingProcessingEventType eventType,
            string? message,
            string? relatedHangfireJobId,
            PostMeetingArtifactLink? artifact,
            string? errorCode = null,
            string? errorMessage = null)
        {
            _dbContext.PostMeetingProcessingEvents.Add(BuildEvent(
                run,
                step,
                eventType,
                step.Status,
                message,
                relatedHangfireJobId,
                artifact,
                errorCode,
                errorMessage));
        }

        private void AddRunStatusEvent(
            PostMeetingProcessingRun run,
            PostMeetingProcessingEventType eventType,
            string? message,
            string? relatedHangfireJobId,
            string? errorCode = null,
            string? errorMessage = null)
        {
            _dbContext.PostMeetingProcessingEvents.Add(BuildEvent(
                run,
                step: null,
                eventType,
                run.Status,
                message,
                relatedHangfireJobId,
                artifact: null,
                errorCode,
                errorMessage));
        }

        private static PostMeetingProcessingEvent BuildEvent(
            PostMeetingProcessingRun run,
            PostMeetingProcessingStep? step,
            PostMeetingProcessingEventType eventType,
            PostMeetingProcessingStatus? status,
            string? message,
            string? relatedHangfireJobId,
            PostMeetingArtifactLink? artifact,
            string? errorCode = null,
            string? errorMessage = null,
            string? metadataJson = null)
        {
            return new PostMeetingProcessingEvent
            {
                OrganizationId = run.OrganizationId,
                MeetingId = run.MeetingId,
                RunId = run.Id,
                StepId = step?.Id,
                StepType = step?.StepType,
                EventType = eventType,
                Status = status,
                RelatedHangfireJobId = relatedHangfireJobId,
                Message = Truncate(message, MaxMessageLength),
                ArtifactType = artifact?.ArtifactType ?? step?.ArtifactType,
                ArtifactId = artifact?.ArtifactId ?? step?.ArtifactId,
                ArtifactIdsJson = SerializeArtifactIds(artifact?.ArtifactIds) ?? step?.ArtifactIdsJson,
                ErrorCode = errorCode,
                ErrorMessage = Truncate(errorMessage, MaxErrorMessageLength),
                MetadataJson = metadataJson,
                OccurredAtUtc = DateTime.UtcNow
            };
        }

        private static void ApplyArtifact(PostMeetingProcessingStep step, PostMeetingArtifactLink? artifact)
        {
            if (artifact is null)
            {
                return;
            }

            step.ArtifactType = artifact.ArtifactType ?? step.ArtifactType;
            step.ArtifactId = artifact.ArtifactId ?? step.ArtifactId;
            step.ArtifactIdsJson = SerializeArtifactIds(artifact.ArtifactIds) ?? step.ArtifactIdsJson;
        }

        private static bool ArtifactMatches(PostMeetingProcessingStep step, PostMeetingArtifactLink? artifact)
        {
            if (artifact is null)
            {
                return true;
            }

            return (artifact.ArtifactType is null || step.ArtifactType == artifact.ArtifactType)
                   && (!artifact.ArtifactId.HasValue || step.ArtifactId == artifact.ArtifactId)
                   && (artifact.ArtifactIds is null || step.ArtifactIdsJson == SerializeArtifactIds(artifact.ArtifactIds));
        }

        private static bool IsOptionalStep(PostMeetingProcessingStepType stepType)
        {
            return stepType is PostMeetingProcessingStepType.ActionExtraction
                or PostMeetingProcessingStepType.TagSuggestion
                or PostMeetingProcessingStepType.KnowledgeIndexing
                or PostMeetingProcessingStepType.ProviderSync
                or PostMeetingProcessingStepType.PersonalizedSummaryGeneration;
        }

        private static bool SameJob(string? currentJobId, string? requestedJobId)
        {
            if (string.IsNullOrWhiteSpace(requestedJobId) && string.IsNullOrWhiteSpace(currentJobId))
            {
                return true;
            }

            if (string.IsNullOrWhiteSpace(requestedJobId) || string.IsNullOrWhiteSpace(currentJobId))
            {
                return false;
            }

            return string.Equals(currentJobId, requestedJobId, StringComparison.Ordinal);
        }

        private static string? SerializeArtifactIds(IReadOnlyCollection<Guid>? artifactIds)
        {
            if (artifactIds is null)
            {
                return null;
            }

            return JsonSerializer.Serialize(artifactIds.OrderBy(x => x).ToArray(), JsonOptions);
        }

        private static string? Truncate(string? value, int maxLength)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return value;
            }

            return value.Length <= maxLength ? value : value[..maxLength];
        }

        private async Task<T> ExecuteWithDuplicateRecoveryAsync<T>(
            Func<CancellationToken, Task<T>> operationAsync,
            CancellationToken cancellationToken)
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                if (attempt > 0)
                {
                    _dbContext.ChangeTracker.Clear();
                }

                try
                {
                    return await operationAsync(cancellationToken);
                }
                catch (DbUpdateException ex) when (attempt == 0 && IsPostMeetingProcessingUniqueViolation(ex))
                {
                }
            }

            throw new InvalidOperationException("Post-meeting processing tracker duplicate recovery failed.");
        }

        private static bool IsPostMeetingProcessingUniqueViolation(DbUpdateException exception)
        {
            if (exception.InnerException is PostgresException postgresException
                && postgresException.SqlState == UniqueViolationSqlState
                && IsPostMeetingProcessingConstraint(postgresException.ConstraintName))
            {
                return true;
            }

            return IsSqlitePostMeetingProcessingUniqueViolation(exception.InnerException);
        }

        private static bool IsPostMeetingProcessingConstraint(string? constraintName)
        {
            return string.Equals(constraintName, StepRunStepTypeUniqueIndex, StringComparison.Ordinal)
                   || string.Equals(constraintName, RunOrgMeetingGenerationUniqueIndex, StringComparison.Ordinal);
        }

        private static bool IsSqlitePostMeetingProcessingUniqueViolation(Exception? exception)
        {
            if (exception?.GetType().FullName != "Microsoft.Data.Sqlite.SqliteException")
            {
                return false;
            }

            var errorCode = exception.GetType().GetProperty("SqliteErrorCode")?.GetValue(exception) as int?;
            if (errorCode != SqliteConstraintViolationErrorCode)
            {
                return false;
            }

            var message = exception.Message;
            return message.Contains(StepRunStepTypeUniqueIndex, StringComparison.OrdinalIgnoreCase)
                   || message.Contains(RunOrgMeetingGenerationUniqueIndex, StringComparison.OrdinalIgnoreCase)
                   || message.Contains("PostMeetingProcessingSteps", StringComparison.OrdinalIgnoreCase)
                   || message.Contains("PostMeetingProcessingRuns", StringComparison.OrdinalIgnoreCase);
        }
    }
}
