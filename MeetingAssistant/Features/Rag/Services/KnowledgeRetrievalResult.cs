using MeetingAssistant.Features.Rag.Models;

namespace MeetingAssistant.Features.Rag.Services
{
    public sealed record KnowledgeRetrievalTagResult(
        Guid Id,
        string Name,
        string? Color);

    public sealed record KnowledgeRetrievalResult(
        Guid ChunkId,
        Guid DocumentId,
        Guid? SourceMeetingId,
        string? SourceMeetingTitle,
        DateTime? SourceMeetingScheduledStartUtc,
        KnowledgeArtifactType SourceType,
        string ChunkText,
        string DocumentTitle,
        string MetadataJson,
        IReadOnlyList<KnowledgeRetrievalTagResult> Tags,
        double VectorDistance,
        int SharedTagCount,
        double TagBoost,
        double RankingScore,
        bool IsTagPreferredResult)
    {
        public Guid? MeetingId => SourceMeetingId;

        public KnowledgeArtifactType ArtifactType => SourceType;

        public string Text => ChunkText;

        public double Distance => VectorDistance;
    }
}
