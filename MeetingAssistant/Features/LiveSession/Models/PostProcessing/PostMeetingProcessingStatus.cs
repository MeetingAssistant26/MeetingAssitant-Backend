namespace MeetingAssistant.Features.LiveSession.Models.PostProcessing
{
    public enum PostMeetingProcessingStatus
    {
        Pending = 0,
        InProgress = 1,
        Completed = 2,
        Failed = 3,
        Skipped = 4,
        CompletedWithWarnings = 5
    }
}
