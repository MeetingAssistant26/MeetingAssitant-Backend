namespace MeetingAssistant.Features.Meetings.Contracts.Requests
{
    public sealed record ApplyMeetingTagSuggestionsRequest(
        IReadOnlyList<Guid> TagIds,
        IReadOnlyList<Guid>? SuggestionIds = null,
        bool RejectUnselectedPending = true);
}
