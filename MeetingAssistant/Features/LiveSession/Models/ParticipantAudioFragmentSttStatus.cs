namespace MeetingAssistant.Features.LiveSession.Models
{
    public enum ParticipantAudioFragmentSttStatus
    {
        NotStarted = 0,
        InProgress = 1,
        Succeeded = 2,
        FailedRetryable = 3,
        FailedTerminal = 4
    }
}
