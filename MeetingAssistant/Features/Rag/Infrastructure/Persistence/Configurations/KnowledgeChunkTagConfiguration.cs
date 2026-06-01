using MeetingAssistant.Features.Rag.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MeetingAssistant.Features.Rag.Infrastructure.Persistence.Configurations
{
    public class KnowledgeChunkTagConfiguration : IEntityTypeConfiguration<KnowledgeChunkTag>
    {
        public void Configure(EntityTypeBuilder<KnowledgeChunkTag> builder)
        {
            builder.HasKey(x => x.Id);

            builder.Property(x => x.TagNameSnapshot)
                .HasMaxLength(80)
                .IsRequired();

            builder.Property(x => x.TagColorSnapshot)
                .HasMaxLength(7)
                .IsRequired(false);

            builder.HasIndex(x => new { x.OrganizationId, x.MeetingTagId, x.KnowledgeChunkId })
                .HasDatabaseName("IX_KnowledgeChunkTags_Org_Tag_Chunk");

            builder.HasIndex(x => new { x.OrganizationId, x.KnowledgeDocumentId })
                .HasDatabaseName("IX_KnowledgeChunkTags_Org_Document");

            builder.HasIndex(x => new { x.KnowledgeChunkId, x.MeetingTagId })
                .IsUnique()
                .HasDatabaseName("UX_KnowledgeChunkTags_Chunk_Tag");

            builder.HasOne(x => x.KnowledgeChunk)
                .WithMany(x => x.Tags)
                .HasForeignKey(x => x.KnowledgeChunkId)
                .OnDelete(DeleteBehavior.Restrict);

            builder.HasOne(x => x.KnowledgeDocument)
                .WithMany()
                .HasForeignKey(x => x.KnowledgeDocumentId)
                .OnDelete(DeleteBehavior.Restrict);

            builder.HasOne(x => x.MeetingTag)
                .WithMany()
                .HasForeignKey(x => x.MeetingTagId)
                .OnDelete(DeleteBehavior.Restrict);
        }
    }
}
