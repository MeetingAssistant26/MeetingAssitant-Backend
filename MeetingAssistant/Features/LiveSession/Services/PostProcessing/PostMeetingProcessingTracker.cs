using System.Text.Json;
using MeetingAssistant.Features.LiveSession.Models.PostProcessing;
using MeetingAssistant.Infrastructure.Persistence.DbContext;
using Microsoft.EntityFrameworkCore;

namespace MeetingAssistant.Features.LiveSession.Services.PostProcessing
{
    public sealed class PostMeetingProcessingTracker(ApplicationDbContext dbContext) : IPostMeetingProcessingTracker
    {
        private const int MaxErrorMessageLength = 2000;
        private const int MaxMessageLength = 2000;

        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

        private readonly ApplicationDbContext _dbContext = dbContext;

        public async Task<PostMeetingProcessingRun> EnsureRunAsync(
            Guid organizationId,
            Guid meetingId,
            Guid? pipelineGenerationId = null,
            string? relatedHangfireJobId = null,
            CancellationToken cancellationToken = default)
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

        public async Task<PostMeetingProcessingStep> MarkStepPendingAsync(
            Guid organizationId,
            Guid meetingId,
            PostMeetingProcessingStepType stepType,
            string? message = null,
            string? relatedHangfireJobId = null,
            PostMeetingArtifactLink? artifact = null,
            CancellationToken cancellationToken = default)
        {
            var run = await EnsureRunAsync(organizationId, meetingId, relatedHangfireJobId: relatedHangfireJobId, cancellationToken: cancellationToken);
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

        public async Task<PostMeetingProcessingStep> StartStepAsync(
            Guid organizationId,
            Guid meetingId,
            PostMeetingProcessingStepType stepType,
            string? relatedHangfireJobId = null,
            string? message = null,
            PostMeetingArtifactLink? artifact = null,
            CancellationToken cancellationToken = default)
        {
            var run = await EnsureRunAsync(organizationId, meetingId, relatedHangfireJobId: relatedHangfireJobId, cancellationToken: cancellationToken);
            var step = await GetOrCreateStepAsync(run, stepType, cancellationToken);

            if (step.Status == PostMeetingProcessingStatus.Completed)
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
            var isRetry = step.Status == PostMeetingProcessingStatus.Failed || step.AttemptCount > 0;

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

            run.RelatedHangfireJobId = relatedHangfireJobId ?? run.RelatedHangfireJobId;

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

        public async Task<PostMeetingProcessingStep> CompleteStepAsync(
            Guid organizationId,
            Guid meetingId,
            PostMeetingProcessingStepType stepType,
            string? relatedHangfireJobId = null,
            string? message = null,
            PostMeetingArtifactLink? artifact = null,
            CancellationToken cancellationToken = default)
        {
            var run = await EnsureRunAsync(organizationId, meetingId, relatedHangfireJobId: relatedHangfireJobId, cancellationToken: cancellationToken);
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
            run.RelatedHangfireJobId = relatedHangfireJobId ?? run.RelatedHangfireJobId;

            AddStepEvent(run, step, PostMeetingProcessingEventType.StepCompleted, message, relatedHangfireJobId, artifact);
            if (artifact is { ArtifactId: not null } || artifact?.ArtifactIds?.Count > 0)
            {
                AddStepEvent(run, step, PostMeetingProcessingEventType.ArtifactLinked, "Step artifacts linked.", relatedHangfireJobId, artifact);
            }

            await _dbContext.SaveChangesAsync(cancellationToken);
            return step;
        }

        public async Task<PostMeetingProcessingStep> FailStepAsync(
            Guid organizationId,
            Guid meetingId,
            PostMeetingProcessingStepType stepType,
            string errorCode,
            string errorMessage,
            string? relatedHangfireJobId = null,
            string? message = null,
            PostMeetingArtifactLink? artifact = null,
            CancellationToken cancellationToken = default)
        {
            var run = await EnsureRunAsync(organizationId, meetingId, relatedHangfireJobId: relatedHangfireJobId, cancellationToken: cancellationToken);
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
                run.RelatedHangfireJobId = relatedHangfireJobId ?? run.RelatedHangfireJobId;
                run.ErrorCode = errorCode;
                run.ErrorMessage = truncatedError;

                AddRunStatusEvent(run, PostMeetingProcessingEventType.RunStatusChanged, "Post-meeting processing failed.", relatedHangfireJobId, errorCode, truncatedError);
            }
            else
            {
                run.StartedAtUtc ??= now;
                run.RelatedHangfireJobId = relatedHangfireJobId ?? run.RelatedHangfireJobId;
            }

            AddStepEvent(run, step, PostMeetingProcessingEventType.StepFailed, message, relatedHangfireJobId, artifact, errorCode, truncatedError);

            await _dbContext.SaveChangesAsync(cancellationToken);
            return step;
        }

        public async Task<PostMeetingProcessingEvent> RecordEventAsync(
            Guid organizationId,
            Guid meetingId,
            PostMeetingProcessingEventType eventType,
            PostMeetingProcessingStepType? stepType = null,
            PostMeetingProcessingStatus? status = null,
            string? message = null,
            string? relatedHangfireJobId = null,
            PostMeetingArtifactLink? artifact = null,
            string? errorCode = null,
            string? errorMessage = null,
            string? metadataJson = null,
            CancellationToken cancellationToken = default)
        {
            var run = await EnsureRunAsync(organizationId, meetingId, relatedHangfireJobId: relatedHangfireJobId, cancellationToken: cancellationToken);
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

        public async Task<PostMeetingProcessingRun> CompleteRunAsync(
            Guid organizationId,
            Guid meetingId,
            string? relatedHangfireJobId = null,
            string? message = null,
            CancellationToken cancellationToken = default)
        {
            var run = await EnsureRunAsync(organizationId, meetingId, relatedHangfireJobId: relatedHangfireJobId, cancellationToken: cancellationToken);

            if (run.Status == PostMeetingProcessingStatus.Completed && SameJob(run.RelatedHangfireJobId, relatedHangfireJobId))
            {
                return run;
            }

            var now = DateTime.UtcNow;
            run.Status = PostMeetingProcessingStatus.Completed;
            run.StartedAtUtc ??= now;
            run.CompletedAtUtc = now;
            run.FailedAtUtc = null;
            run.RelatedHangfireJobId = relatedHangfireJobId ?? run.RelatedHangfireJobId;
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
                RelatedHangfireJobId = relatedHangfireJobId ?? step?.RelatedHangfireJobId ?? run.RelatedHangfireJobId,
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
                or PostMeetingProcessingStepType.ProviderSync;
        }

        private static bool SameJob(string? currentJobId, string? requestedJobId)
        {
            return string.IsNullOrWhiteSpace(requestedJobId)
                   || string.Equals(currentJobId, requestedJobId, StringComparison.Ordinal);
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
    }
}
