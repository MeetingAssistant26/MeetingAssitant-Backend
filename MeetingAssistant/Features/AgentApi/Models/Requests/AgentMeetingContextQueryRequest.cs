namespace MeetingAssistant.Features.AgentApi.Models.Requests
{
    public sealed record AgentMeetingContextQueryRequest(
        string Question,
        string? Transcript = null,
        int? TopK = null,
        IReadOnlyCollection<Guid>? PreferredTagIds = null,
        IReadOnlyCollection<string>? SourceTypes = null);
}
