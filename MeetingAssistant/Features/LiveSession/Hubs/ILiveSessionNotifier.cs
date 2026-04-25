namespace MeetingAssistant.Features.LiveSession.Hubs
{
    public interface ILiveSessionNotifier
    {
        Task NotifySessionStartedAsync(
            Guid organizationId,
            Guid meetingId,
            DateTime occurredAtUtc,
            CancellationToken cancellationToken = default);

        Task NotifySessionEndedAsync(
            Guid organizationId,
            Guid meetingId,
            DateTime occurredAtUtc,
            CancellationToken cancellationToken = default);

        Task NotifyParticipantJoinedAsync(
            Guid organizationId,
            Guid meetingId,
            Guid participantUserId,
            DateTime occurredAtUtc,
            CancellationToken cancellationToken = default);

        Task NotifyParticipantLeftAsync(
            Guid organizationId,
            Guid meetingId,
            Guid participantUserId,
            DateTime occurredAtUtc,
            CancellationToken cancellationToken = default);
    }
}
