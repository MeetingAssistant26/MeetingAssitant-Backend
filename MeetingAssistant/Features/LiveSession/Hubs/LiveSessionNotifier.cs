using Microsoft.AspNetCore.SignalR;

namespace MeetingAssistant.Features.LiveSession.Hubs
{
    public class LiveSessionNotifier(IHubContext<LiveSessionHub> hubContext) : ILiveSessionNotifier
    {
        private readonly IHubContext<LiveSessionHub> _hubContext = hubContext;

        public Task NotifySessionStartedAsync(
            Guid organizationId,
            Guid meetingId,
            DateTime occurredAtUtc,
            CancellationToken cancellationToken = default)
        {
            return _hubContext.Clients
                .Group($"org:{organizationId}")
                .SendAsync("session.started", new { meetingId, occurredAtUtc }, cancellationToken);
        }

        public Task NotifySessionEndedAsync(
            Guid organizationId,
            Guid meetingId,
            DateTime occurredAtUtc,
            CancellationToken cancellationToken = default)
        {
            return _hubContext.Clients
                .Group($"org:{organizationId}")
                .SendAsync("session.ended", new { meetingId, occurredAtUtc }, cancellationToken);
        }

        public Task NotifyParticipantJoinedAsync(
            Guid organizationId,
            Guid meetingId,
            Guid participantUserId,
            DateTime occurredAtUtc,
            CancellationToken cancellationToken = default)
        {
            return _hubContext.Clients
                .Group($"org:{organizationId}")
                .SendAsync("participant.joined", new { meetingId, participantUserId, occurredAtUtc }, cancellationToken);
        }

        public Task NotifyParticipantLeftAsync(
            Guid organizationId,
            Guid meetingId,
            Guid participantUserId,
            DateTime occurredAtUtc,
            CancellationToken cancellationToken = default)
        {
            return _hubContext.Clients
                .Group($"org:{organizationId}")
                .SendAsync("participant.left", new { meetingId, participantUserId, occurredAtUtc }, cancellationToken);
        }
    }
}
