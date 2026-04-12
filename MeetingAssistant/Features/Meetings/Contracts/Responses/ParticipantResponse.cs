using MeetingAssistant.Features.Meetings.Models;

namespace MeetingAssistant.Features.Meetings.Contracts.Responses
{
    public sealed record ParticipantResponse(
        Guid Id,
        Guid MeetingId,
        Guid UserId,
        string DisplayName,
        string Email,
        MeetingRole Role,
        DateTime CreatedAtUtc);
}