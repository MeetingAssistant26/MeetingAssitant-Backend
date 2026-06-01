using MeetingAssistant.Features.Meetings.Models;

namespace MeetingAssistant.Features.Meetings.Contracts.Requests
{
    public sealed record UpdateParticipantRoleRequest(MeetingRole? MeetingRole);
}
