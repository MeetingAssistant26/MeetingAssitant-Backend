using MeetingAssistant.Features.Rag.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MeetingAssistant.Features.Rag.Infrastructure.Persistence.Configurations
{
    public class KnowledgeChunkConfiguration : IEntityTypeConfiguration<KnowledgeChunk>
    {
        public void Configure(EntityTypeBuilder<KnowledgeChunk> builder)
        {
            builder.HasKey(x => x.Id);

            builder.Property(x => x.ArtifactType)
                .HasConversion<int>()
                .IsRequired();

            builder.Property(x => x.ArtifactVersion)
                .IsRequired();

            builder.Property(x => x.Text)
                .IsRequired();

            builder.Property(x => x.ContentHash)
                .HasMaxLength(128)
                .IsRequired();

            builder.Property(x => x.EmbeddingProvider)
                .HasMaxLength(80)
                .IsRequired();

            builder.Property(x => x.EmbeddingModel)
                .HasMaxLength(160)
                .IsRequired();

            builder.Property(x => x.EmbeddingDimension)
                .IsRequired();

            builder.Property(x => x.Visibility)
                .HasConversion<int>()
                .IsRequired();

            builder.Property(x => x.MetadataJson)
                .HasColumnType("jsonb")
                .IsRequired();

            builder.Property(x => x.GeneratedAtUtc)
                .HasColumnType("timestamp with time zone")
                .IsRequired();

            builder.Property(x => x.EmbeddingVectorText)
                .HasColumnName("Embedding")
                .HasColumnType("vector")
                .IsRequired();

            builder.HasIndex(x => new { x.OrganizationId, x.Visibility, x.IsCurrent, x.EmbeddingModel, x.EmbeddingDimension })
                .HasDatabaseName("IX_KnowledgeChunks_Org_Visibility_Current_Model_Dim");

            builder.HasIndex(x => new { x.OrganizationId, x.MeetingId })
                .HasDatabaseName("IX_KnowledgeChunks_Org_Meeting");

            builder.HasIndex(x => new { x.OrganizationId, x.ArtifactType, x.ArtifactId, x.ArtifactVersion })
                .HasDatabaseName("IX_KnowledgeChunks_Org_Artifact");

            builder.HasIndex(x => new { x.OrganizationId, x.IndexGenerationId })
                .HasDatabaseName("IX_KnowledgeChunks_Org_IndexGeneration");

            builder.HasIndex(x => new { x.OrganizationId, x.ContentHash })
                .HasDatabaseName("IX_KnowledgeChunks_Org_ContentHash");

            builder.HasIndex(x => new { x.DocumentId, x.ChunkIndex })
                .IsUnique()
                .HasDatabaseName("UX_KnowledgeChunks_Document_ChunkIndex");

            builder.HasOne(x => x.Document)
                .WithMany(x => x.Chunks)
                .HasForeignKey(x => x.DocumentId)
                .OnDelete(DeleteBehavior.Restrict);

            builder.HasOne(x => x.Meeting)
                .WithMany()
                .HasForeignKey(x => x.MeetingId)
                .OnDelete(DeleteBehavior.Restrict);
        }
    }
}
