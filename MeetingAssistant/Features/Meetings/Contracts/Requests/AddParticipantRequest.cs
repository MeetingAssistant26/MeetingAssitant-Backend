using MeetingAssistant.Features.Meetings.Models;

namespace MeetingAssistant.Features.Meetings.Contracts.Requests
{
    public sealed record AddParticipantRequest(
        Guid UserId,
        MeetingRole MeetingRole);
}