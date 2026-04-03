using MeetingAssistant.Shared.Abstractions;

namespace MeetingAssistant.Features.Organizations.Models.Events
{
    public record OrganizationCreatedEvent(Guid OrganizationId, Guid CreatedByUserId) : IDomainEvent;
    public record MemberJoinedEvent(Guid OrganizationId, Guid UserId, OrganizationRole Role) : IDomainEvent;
    public record RoleChangedEvent(Guid OrganizationId, Guid UserId, OrganizationRole OldRole, OrganizationRole NewRole) : IDomainEvent;
    public record MemberContextUpdatedEvent(Guid OrganizationId, Guid UserId) : IDomainEvent;
    public record MemberLeftEvent(Guid OrganizationId, Guid UserId) : IDomainEvent;
    public record InvitationRevokedEvent(Guid OrganizationId, Guid InvitationId) : IDomainEvent;
}
