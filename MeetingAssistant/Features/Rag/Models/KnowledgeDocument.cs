using MeetingAssistant.Api.Shared;
using MeetingAssistant.Features.Meetings.Models;
using MeetingAssistant.Shared.Abstractions;

namespace MeetingAssistant.Features.Rag.Models
{
    public class KnowledgeDocument : BaseEntity, IHasOrganizationId
    {
        public Guid OrganizationId { get; set; }

        public Guid? MeetingId { get; set; }
        public Meeting? Meeting { get; set; }

        public KnowledgeArtifactType ArtifactType { get; set; }

        public Guid ArtifactId { get; set; }

        public int ArtifactVersion { get; set; } = 1;

        public string Title { get; set; } = string.Empty;

        public string ContentHash { get; set; } = string.Empty;

        public Guid IndexGenerationId { get; set; }

        public KnowledgeVisibility Visibility { get; set; } = KnowledgeVisibility.Draft;

        public bool IsCurrent { get; set; }

        public string EmbeddingProvider { get; set; } = string.Empty;

        public string EmbeddingModel { get; set; } = string.Empty;

        public int EmbeddingDimension { get; set; }

        public string MetadataJson { get; set; } = "{}";

        public DateTime GeneratedAtUtc { get; set; }

        public Guid? SourceTranscriptId { get; set; }

        public string? SourceTranscriptHash { get; set; }

        public int? SourceTranscriptRevision { get; set; }

        public DateTime? SourceTranscriptGeneratedAtUtc { get; set; }

        public ICollection<KnowledgeChunk> Chunks { get; set; } = new List<KnowledgeChunk>();
    }
}
