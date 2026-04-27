using MeetingAssistant.Shared.Abstractions;

namespace MeetingAssistant.Features.LiveSession.Models.Events
{
    public record ParticipantAudioReadyEvent(Guid MeetingId, Guid OrganizationId, DateTime OccurredAtUtc) : IDomainEvent;

    public record MeetingTranscriptReadyEvent(Guid MeetingId, Guid OrganizationId, DateTime OccurredAtUtc) : IDomainEvent;
}
