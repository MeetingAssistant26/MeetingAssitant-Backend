using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MeetingAssistant.Features.Organizations.Models;

namespace MeetingAssistant.Features.Organizations.Infrastructure.Persistence.Configurations
{
    public class MeetingTagConfiguration : IEntityTypeConfiguration<MeetingTag>
    {
        public void Configure(EntityTypeBuilder<MeetingTag> builder)
        {
            builder.HasKey(t => t.Id);

            builder.Property(t => t.Name)
                .IsRequired()
                .HasMaxLength(50);

            builder.Property(t => t.Color)
                .HasMaxLength(7);

            // Case-insensitive unique index via raw SQL (EF doesn't support LOWER in indexes natively).
            // Applied in migration as: CREATE UNIQUE INDEX ... ON "MeetingTags" ("OrganizationId", LOWER("Name")) WHERE "IsActive" = true
            builder.HasIndex(t => new { t.OrganizationId, t.Name })
                .IsUnique()
                .HasFilter("\"IsActive\" = true")
                .HasDatabaseName("IX_MeetingTags_OrgId_LowerName_ActiveOnly");

            builder.HasIndex(t => t.OrganizationId)
                .HasDatabaseName("IX_MeetingTags_OrganizationId");
        }
    }
}
