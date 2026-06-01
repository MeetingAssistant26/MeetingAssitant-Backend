namespace MeetingAssistant.Features.Rag.Services
{
    public sealed record KnowledgeRetrievalRequest(
        Guid OrganizationId,
        string QueryText,
        int TopK = 5,
        Guid? CurrentMeetingId = null,
        IReadOnlyCollection<Guid>? PreferredTagIds = null,
        string? EmbeddingModel = null,
        int? EmbeddingDimension = null);
}
