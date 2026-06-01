namespace MeetingAssistant.Features.AgentApi.Models.Responses
{
    public sealed record AgentMeetingContextQueryResponse(
        Guid MeetingId,
        int TopK,
        IReadOnlyList<AgentMeetingContextSnippetResponse> Snippets);

    public sealed record AgentMeetingContextSnippetResponse(
        Guid ChunkId,
        Guid DocumentId,
        AgentMeetingContextSourceResponse Source,
        string DocumentTitle,
        string ChunkText,
        IReadOnlyList<AgentMeetingContextTagResponse> Tags,
        AgentMeetingContextScoreResponse Score);

    public sealed record AgentMeetingContextSourceResponse(
        Guid? MeetingId,
        string? MeetingTitle,
        DateTime? MeetingScheduledStartUtc,
        string Type);

    public sealed record AgentMeetingContextTagResponse(
        Guid Id,
        string Name,
        string? Color);

    public sealed record AgentMeetingContextScoreResponse(
        double VectorDistance,
        int SharedTagCount,
        double TagBoost,
        double RankingScore,
        bool IsTagPreferredResult);
}
