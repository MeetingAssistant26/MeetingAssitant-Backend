namespace MeetingAssistant.Features.LiveSession.Contracts.AiDebug
{
    public sealed record PostMeetingProcessingTraceResponse(
        bool Enabled,
        IReadOnlyList<PostMeetingProcessingRunTraceResponse> Runs,
        IReadOnlyList<PostMeetingProcessingStepTraceResponse> Steps,
        IReadOnlyList<PostMeetingProcessingEventTraceResponse> Events);

    public sealed record PostMeetingProcessingRunTraceResponse(
        Guid Id,
        Guid MeetingId,
        Guid PipelineGenerationId,
        string Status,
        int AttemptCount,
        DateTime? StartedAtUtc,
        DateTime? CompletedAtUtc,
        DateTime? FailedAtUtc,
        string? RelatedHangfireJobId,
        PostMeetingProcessingErrorDto? Error,
        DateTime CreatedAtUtc,
        DateTime? UpdatedAtUtc);

    public sealed record PostMeetingProcessingStepTraceResponse(
        Guid Id,
        Guid RunId,
        Guid MeetingId,
        string StepType,
        string Status,
        int AttemptCount,
        DateTime? LastAttemptAtUtc,
        DateTime? StartedAtUtc,
        DateTime? CompletedAtUtc,
        DateTime? FailedAtUtc,
        string? RelatedHangfireJobId,
        PostMeetingProcessingArtifactDto? Artifact,
        PostMeetingProcessingErrorDto? Error,
        DateTime CreatedAtUtc,
        DateTime? UpdatedAtUtc);

    public sealed record PostMeetingProcessingEventTraceResponse(
        Guid Id,
        Guid RunId,
        Guid? StepId,
        Guid MeetingId,
        string? StepType,
        string EventType,
        string? Status,
        DateTime OccurredAtUtc,
        string? RelatedHangfireJobId,
        string? Message,
        PostMeetingProcessingArtifactDto? Artifact,
        PostMeetingProcessingErrorDto? Error,
        DateTime CreatedAtUtc,
        DateTime? UpdatedAtUtc);

    public sealed record PostMeetingProcessingArtifactDto(
        string? Type,
        Guid? Id,
        IReadOnlyList<Guid> Ids);

    public sealed record PostMeetingProcessingErrorDto(
        string? Code,
        string? Message);
}
