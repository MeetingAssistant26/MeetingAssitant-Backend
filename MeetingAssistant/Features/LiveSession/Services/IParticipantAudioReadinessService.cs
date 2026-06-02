using MeetingAssistant.Features.LiveSession.Models.Events;

namespace MeetingAssistant.Features.LiveSession.Services
{
    public interface IParticipantAudioReadinessService
    {
        Task<ParticipantAudioReadyEvent?> TryCreateReadyEventAsync(
            Guid meetingId,
            Guid organizationId,
            CancellationToken cancellationToken = default);
    }
}
