namespace MeetingAssistant.Features.LiveSession.Services
{
    public interface IEgressService
    {
        Task StartTrackEgressAsync(Guid meetingId, string roomName, string trackId, string participantIdentity, CancellationToken ct = default);
    }
}
