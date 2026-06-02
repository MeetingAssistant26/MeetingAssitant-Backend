namespace MeetingAssistant.Features.LiveSession.Models.PostProcessing
{
    public enum PostMeetingProcessingEventType
    {
        RunCreated = 0,
        RunStatusChanged = 1,
        StepPending = 2,
        StepStarted = 3,
        StepCompleted = 4,
        StepFailed = 5,
        StepRetried = 6,
        ArtifactLinked = 7,
        Info = 8,
        Error = 9,
        StepSkipped = 10
    }
}
