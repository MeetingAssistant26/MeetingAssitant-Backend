using MeetingAssistant.Shared.Abstractions;

namespace MeetingAssistant.Features.LiveSession.Models.Events
{
    public record SessionStartedEvent : IDomainEvent;

    public record SessionEndedEvent : IDomainEvent;
}
