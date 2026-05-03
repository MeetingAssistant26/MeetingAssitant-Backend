using MeetingAssistant.Shared.Abstractions;

namespace MeetingAssistant.Features.Tasks.Models.Events
{
    public record ReminderCreatedEvent(Guid OrganizationId, Guid ReminderId) : IDomainEvent;

    public record ReminderDeliveredEvent(Guid OrganizationId, Guid ReminderId) : IDomainEvent;

    public record ReminderCancelledEvent(Guid OrganizationId, Guid ReminderId) : IDomainEvent;
}
