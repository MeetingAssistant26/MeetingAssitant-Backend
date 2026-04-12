namespace MeetingAssistant.Features.Meetings.Contracts.Responses
{
    public sealed record ConflictResponse(
        Guid UserId,
        string DisplayName,
        List<MeetingResponse> ConflictingMeetings);
}