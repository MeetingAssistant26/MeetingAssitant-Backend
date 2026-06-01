using MeetingAssistant.Features.Rag.Models;

namespace MeetingAssistant.Features.Rag.Services
{
    public sealed record KnowledgeRetrievalResult(
        Guid ChunkId,
        Guid DocumentId,
        Guid? MeetingId,
        KnowledgeArtifactType ArtifactType,
        string Text,
        string DocumentTitle,
        string MetadataJson,
        double Distance,
        int SharedTagCount,
        double RankingScore);
}
