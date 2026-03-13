using MeetingAssistant.Shared.Abstractions;

namespace MeetingAssistant.Infrastructure.Events
{
    public sealed record InfrastructureReadyEvent(string Message) : IDomainEvent;
}
