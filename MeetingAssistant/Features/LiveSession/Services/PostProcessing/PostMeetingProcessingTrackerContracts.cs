using MeetingAssistant.Features.LiveSession.Models.PostProcessing;

namespace MeetingAssistant.Features.LiveSession.Services.PostProcessing
{
    public sealed record PostMeetingArtifactLink(
        string? ArtifactType = null,
        Guid? ArtifactId = null,
        IReadOnlyCollection<Guid>? ArtifactIds = null);

    public sealed record PostMeetingProcessingSnapshot(
        PostMeetingProcessingRun? Run,
        IReadOnlyList<PostMeetingProcessingStep> Steps,
        IReadOnlyList<PostMeetingProcessingEvent> Events);

    public interface IPostMeetingProcessingTracker
    {
        Task<PostMeetingProcessingRun> EnsureRunAsync(
            Guid organizationId,
            Guid meetingId,
            Guid? pipelineGenerationId = null,
            string? relatedHangfireJobId = null,
            CancellationToken cancellationToken = default);

        Task<PostMeetingProcessingStep> MarkStepPendingAsync(
            Guid organizationId,
            Guid meetingId,
            PostMeetingProcessingStepType stepType,
            Guid? pipelineGenerationId = null,
            string? message = null,
            string? relatedHangfireJobId = null,
            PostMeetingArtifactLink? artifact = null,
            CancellationToken cancellationToken = default);

        Task<PostMeetingProcessingStep> StartStepAsync(
            Guid organizationId,
            Guid meetingId,
            PostMeetingProcessingStepType stepType,
            Guid? pipelineGenerationId = null,
            string? relatedHangfireJobId = null,
            string? message = null,
            PostMeetingArtifactLink? artifact = null,
            CancellationToken cancellationToken = default);

        Task<PostMeetingProcessingStep> CompleteStepAsync(
            Guid organizationId,
            Guid meetingId,
            PostMeetingProcessingStepType stepType,
            Guid? pipelineGenerationId = null,
            string? relatedHangfireJobId = null,
            string? message = null,
            PostMeetingArtifactLink? artifact = null,
            CancellationToken cancellationToken = default);

        Task<PostMeetingProcessingStep> CompleteStepWithWarningsAsync(
            Guid organizationId,
            Guid meetingId,
            PostMeetingProcessingStepType stepType,
            Guid? pipelineGenerationId = null,
            string? relatedHangfireJobId = null,
            string? message = null,
            PostMeetingArtifactLink? artifact = null,
            CancellationToken cancellationToken = default);

        Task<PostMeetingProcessingStep> FailStepAsync(
            Guid organizationId,
            Guid meetingId,
            PostMeetingProcessingStepType stepType,
            string errorCode,
            string errorMessage,
            Guid? pipelineGenerationId = null,
            string? relatedHangfireJobId = null,
            string? message = null,
            PostMeetingArtifactLink? artifact = null,
            CancellationToken cancellationToken = default);

        Task<PostMeetingProcessingStep> SkipStepAsync(
            Guid organizationId,
            Guid meetingId,
            PostMeetingProcessingStepType stepType,
            Guid? pipelineGenerationId = null,
            string? relatedHangfireJobId = null,
            string? message = null,
            PostMeetingArtifactLink? artifact = null,
            CancellationToken cancellationToken = default);

        Task<PostMeetingProcessingEvent> RecordEventAsync(
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
            CancellationToken cancellationToken = default);

        Task<PostMeetingProcessingRun> CompleteRunAsync(
            Guid organizationId,
            Guid meetingId,
            Guid? pipelineGenerationId = null,
            string? relatedHangfireJobId = null,
            string? message = null,
            CancellationToken cancellationToken = default);

        Task<PostMeetingProcessingRun> CompleteRunWithWarningsAsync(
            Guid organizationId,
            Guid meetingId,
            Guid? pipelineGenerationId = null,
            string? relatedHangfireJobId = null,
            string? message = null,
            CancellationToken cancellationToken = default);

        Task<PostMeetingProcessingSnapshot> GetLatestByMeetingAsync(
            Guid organizationId,
            Guid meetingId,
            CancellationToken cancellationToken = default);
    }
}
