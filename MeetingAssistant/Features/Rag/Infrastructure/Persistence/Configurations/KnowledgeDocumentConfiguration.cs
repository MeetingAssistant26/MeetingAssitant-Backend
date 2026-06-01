using MeetingAssistant.Features.Rag.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MeetingAssistant.Features.Rag.Infrastructure.Persistence.Configurations
{
    public class KnowledgeDocumentConfiguration : IEntityTypeConfiguration<KnowledgeDocument>
    {
        public void Configure(EntityTypeBuilder<KnowledgeDocument> builder)
        {
            builder.HasKey(x => x.Id);

            builder.Property(x => x.ArtifactType)
                .HasConversion<int>()
                .IsRequired();

            builder.Property(x => x.ArtifactVersion)
                .IsRequired();

            builder.Property(x => x.Title)
                .HasMaxLength(300)
                .IsRequired();

            builder.Property(x => x.ContentHash)
                .HasMaxLength(128)
                .IsRequired();

            builder.Property(x => x.Visibility)
                .HasConversion<int>()
                .IsRequired();

            builder.Property(x => x.EmbeddingProvider)
                .HasMaxLength(80)
                .IsRequired();

            builder.Property(x => x.EmbeddingModel)
                .HasMaxLength(160)
                .IsRequired();

            builder.Property(x => x.EmbeddingDimension)
                .IsRequired();

            builder.Property(x => x.MetadataJson)
                .HasColumnType("jsonb")
                .IsRequired();

            builder.Property(x => x.GeneratedAtUtc)
                .HasColumnType("timestamp with time zone")
                .IsRequired();

            builder.HasIndex(x => new { x.OrganizationId, x.Visibility, x.IsCurrent, x.MeetingId })
                .HasDatabaseName("IX_KnowledgeDocuments_Org_Visibility_Current_Meeting");

            builder.HasIndex(x => new { x.OrganizationId, x.ArtifactType, x.ArtifactId, x.ArtifactVersion })
                .HasDatabaseName("IX_KnowledgeDocuments_Org_Artifact");

            builder.HasIndex(x => new { x.OrganizationId, x.IndexGenerationId })
                .HasDatabaseName("IX_KnowledgeDocuments_Org_IndexGeneration");

            builder.HasIndex(x => new { x.OrganizationId, x.ContentHash })
                .HasDatabaseName("IX_KnowledgeDocuments_Org_ContentHash");

            builder.HasIndex(x => new { x.OrganizationId, x.ArtifactType, x.ArtifactId, x.ArtifactVersion, x.IndexGenerationId })
                .IsUnique()
                .HasDatabaseName("UX_KnowledgeDocuments_Org_Artifact_Generation");

            builder.HasOne(x => x.Meeting)
                .WithMany()
                .HasForeignKey(x => x.MeetingId)
                .OnDelete(DeleteBehavior.Restrict);
        }
    }
}
