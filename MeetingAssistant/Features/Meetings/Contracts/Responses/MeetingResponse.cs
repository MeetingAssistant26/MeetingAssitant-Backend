using MeetingAssistant.Features.Meetings.Models;

namespace MeetingAssistant.Features.Meetings.Contracts.Responses
{
    public sealed record MeetingResponse(
        Guid Id,
        string Title,
        string? Description,
        DateTime ScheduledStartUtc,
        DateTime ScheduledEndUtc,
        MeetingStatus Status,
        List<ParticipantResponse> Participants,
        List<Guid> TagIds);
}