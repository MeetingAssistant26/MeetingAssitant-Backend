using MeetingAssistant.Shared.Abstractions;

namespace MeetingAssistant.Features.Identity.Events
{
    public record UserRegisteredEvent(Guid UserId, string Email) : IDomainEvent;
    public record UserLoggedInEvent(Guid UserId) : IDomainEvent;
    public record TokenRefreshedEvent(Guid UserId, string RefreshTokenFamilyId) : IDomainEvent;
    public record UserLoggedOutEvent(Guid UserId) : IDomainEvent;
    public record PasswordChangedEvent(Guid UserId) : IDomainEvent;
    public record RefreshTokenCompromiseDetectedEvent(Guid UserId, string FamilyId) : IDomainEvent;
}
