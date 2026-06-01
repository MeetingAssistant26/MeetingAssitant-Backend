namespace MeetingAssistant.Features.Meetings.Contracts.Responses
{
    public sealed record MeetingTagSuggestionResponse(
        Guid Id,
        Guid MeetingId,
        Guid MeetingTagId,
        string TagName,
        string? TagColor,
        decimal? Confidence,
        string? Reason,
        string Status,
        DateTime SuggestedAtUtc,
        Guid? TranscriptId,
        Guid? SummaryId,
        bool KnowledgeRefreshPending,
        bool UsesOnlyConfirmedTagsForRag);

    public sealed record MeetingTagSuggestionListResponse(
        IReadOnlyList<MeetingTagSuggestionResponse> Items,
        IReadOnlyList<Guid> ConfirmedTagIds,
        bool KnowledgeRefreshPending);

    public sealed record MeetingTagSuggestionApplyResponse(
        IReadOnlyList<Guid> ConfirmedTagIds,
        IReadOnlyList<MeetingTagSuggestionResponse> Suggestions,
        string? ReindexJobId,
        bool ReindexEnqueued);

    public sealed record MeetingTagMetadataRefreshResponse(string ReindexJobId, bool ReindexEnqueued);
}
