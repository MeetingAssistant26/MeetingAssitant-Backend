using MeetingAssistant.Api.Shared;
using MeetingAssistant.Features.Organizations.Models;
using MeetingAssistant.Shared.Abstractions;

namespace MeetingAssistant.Features.Rag.Models
{
    public class KnowledgeChunkTag : BaseEntity, IHasOrganizationId
    {
        public Guid OrganizationId { get; set; }

        public Guid KnowledgeChunkId { get; set; }
        public KnowledgeChunk KnowledgeChunk { get; set; } = default!;

        public Guid KnowledgeDocumentId { get; set; }
        public KnowledgeDocument KnowledgeDocument { get; set; } = default!;

        public Guid MeetingTagId { get; set; }
        public MeetingTag MeetingTag { get; set; } = default!;

        public string TagNameSnapshot { get; set; } = string.Empty;

        public string? TagColorSnapshot { get; set; }
    }
}
