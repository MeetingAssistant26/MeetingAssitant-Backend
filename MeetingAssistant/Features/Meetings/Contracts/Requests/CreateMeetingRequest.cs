using MeetingAssistant.Features.Meetings.Models;

namespace MeetingAssistant.Features.Meetings.Contracts.Requests
{
    public sealed record CreateMeetingRequest(
        string Title,
        string? Description,
        DateTime ScheduledStartUtc,
        DateTime ScheduledEndUtc,
        List<Guid>? TagIds)
    {
        public List<CreateMeetingParticipantRequest>? Participants { get; init; }
    }

    public sealed record CreateMeetingParticipantRequest(
        Guid UserId,
        MeetingRole MeetingRole);
}
