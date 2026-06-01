namespace MeetingAssistant.Features.Rag.Services
{
    public sealed record ReindexMeetingKnowledgeResult(
        Guid IndexGenerationId,
        int PublishedDocumentCount,
        int PublishedChunkCount,
        IReadOnlyList<Guid> PublishedDocumentIds);
}
