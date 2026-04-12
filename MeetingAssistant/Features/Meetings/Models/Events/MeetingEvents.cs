using MeetingAssistant.Shared.Abstractions;

namespace MeetingAssistant.Features.Meetings.Models.Events
{
    public record MeetingCreatedEvent(Guid OrganizationId, Guid MeetingId) : IDomainEvent;
    
    public record MeetingUpdatedEvent(Guid OrganizationId, Guid MeetingId) : IDomainEvent;
    
    public record MeetingCancelledEvent(Guid OrganizationId, Guid MeetingId) : IDomainEvent;
    
    public record MeetingStartedEvent(Guid OrganizationId, Guid MeetingId) : IDomainEvent;
    
    public record MeetingEndedEvent(Guid OrganizationId, Guid MeetingId) : IDomainEvent;
}