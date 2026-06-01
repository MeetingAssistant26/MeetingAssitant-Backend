namespace MeetingAssistant.Features.Meetings.Contracts.Requests
{
    public sealed record RejectMeetingTagSuggestionRequest(string? Reason = null);
}
