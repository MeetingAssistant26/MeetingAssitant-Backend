namespace MeetingAssistant.Features.LiveSession.Models
{
    public enum SessionEventType
    {
        RoomStarted = 0,
        RoomFinished = 1,
        ParticipantJoined = 2,
        ParticipantLeft = 3,
        RecordingStarted = 4,
        EgressEnded = 5,
        ParticipantAudioReady = 6,
        TrackPublished = 7,
        Unknown = 99
    }
}
