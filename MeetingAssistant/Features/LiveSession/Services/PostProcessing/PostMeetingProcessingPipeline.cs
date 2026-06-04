using MeetingAssistant.Features.LiveSession.Models.PostProcessing;

namespace MeetingAssistant.Features.LiveSession.Services.PostProcessing;

public static class PostMeetingProcessingPipeline
{
    public static async Task<Guid> ResolveAutomaticPipelineGenerationIdAsync(
        IPostMeetingProcessingTracker tracker,
        Guid organizationId,
        Guid meetingId,
        CancellationToken cancellationToken = default)
    {
        var run = await tracker.EnsureRunAsync(
            organizationId,
            meetingId,
            cancellationToken: cancellationToken);

        return run.PipelineGenerationId;
    }

    public static async Task<Guid> BeginManualRerunAsync(
        IPostMeetingProcessingTracker tracker,
        Guid organizationId,
        Guid meetingId,
        PostMeetingProcessingStepType stepType,
        string? message = null,
        string? relatedHangfireJobId = null,
        CancellationToken cancellationToken = default)
    {
        var pipelineGenerationId = Guid.NewGuid();
        await tracker.EnsureRunAsync(
            organizationId,
            meetingId,
            pipelineGenerationId,
            relatedHangfireJobId,
            cancellationToken);

        await tracker.MarkStepPendingAsync(
            organizationId,
            meetingId,
            stepType,
            pipelineGenerationId,
            message,
            relatedHangfireJobId,
            cancellationToken: cancellationToken);

        return pipelineGenerationId;
    }
}
