namespace MeetingAssistant.Features.Meetings.Contracts.Requests
{
    public sealed record UpdateMeetingTagSuggestionRequest(
        Guid? MeetingTagId = null,
        decimal? Confidence = null,
        string? Reason = null);
}
