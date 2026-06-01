using MeetingAssistant.Api.Shared;
using MeetingAssistant.Features.Meetings.Models;
using MeetingAssistant.Shared.Abstractions;

namespace MeetingAssistant.Features.Rag.Models
{
    public class KnowledgeChunk : BaseEntity, IHasOrganizationId
    {
        public Guid OrganizationId { get; set; }

        public Guid DocumentId { get; set; }
        public KnowledgeDocument Document { get; set; } = default!;

        public Guid? MeetingId { get; set; }
        public Meeting? Meeting { get; set; }

        public KnowledgeArtifactType ArtifactType { get; set; }

        public Guid ArtifactId { get; set; }

        public int ArtifactVersion { get; set; } = 1;

        public int ChunkIndex { get; set; }

        public string Text { get; set; } = string.Empty;

        public int CharacterCount { get; set; }

        public int? TokenCount { get; set; }

        public string ContentHash { get; set; } = string.Empty;

        public string EmbeddingProvider { get; set; } = string.Empty;

        public string EmbeddingModel { get; set; } = string.Empty;

        public int EmbeddingDimension { get; set; }

        public Guid IndexGenerationId { get; set; }

        public KnowledgeVisibility Visibility { get; set; } = KnowledgeVisibility.Draft;

        public bool IsCurrent { get; set; }

        public string MetadataJson { get; set; } = "{}";

        public DateTime GeneratedAtUtc { get; set; }

        /// <summary>
        /// PostgreSQL pgvector storage. The value is formatted as a pgvector literal
        /// (for example, "[0.1,0.2]") so this schema does not require a client-side
        /// pgvector binary type adapter. Retrieval writes query vectors as text and
        /// casts them to vector in SQL.
        /// </summary>
        public string EmbeddingVectorText { get; set; } = "[]";

        public ICollection<KnowledgeChunkTag> Tags { get; set; } = new List<KnowledgeChunkTag>();
    }
}
