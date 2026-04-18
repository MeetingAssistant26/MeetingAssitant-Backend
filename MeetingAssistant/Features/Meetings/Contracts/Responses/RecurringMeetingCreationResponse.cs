namespace MeetingAssistant.Features.Meetings.Contracts.Responses
{
    public sealed record RecurringMeetingCreationResponse(
        int Count,
        IReadOnlyList<MeetingResponse> Meetings);
}