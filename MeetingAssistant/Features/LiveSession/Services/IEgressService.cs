namespace MeetingAssistant.Features.LiveSession.Services
{
    public sealed record EgressStartResult(string EgressId, string StorageObjectKey);

    public interface IEgressService
    {
        Task<EgressStartResult> StartTrackEgressAsync(Guid meetingId, string roomName, string trackId, string participantIdentity, CancellationToken ct = default);
    }
}
