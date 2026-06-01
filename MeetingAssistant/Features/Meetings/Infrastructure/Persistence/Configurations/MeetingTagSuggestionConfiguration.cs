using MeetingAssistant.Features.Meetings.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MeetingAssistant.Features.Meetings.Infrastructure.Persistence.Configurations
{
    public sealed class MeetingTagSuggestionConfiguration : IEntityTypeConfiguration<MeetingTagSuggestion>
    {
        public void Configure(EntityTypeBuilder<MeetingTagSuggestion> builder)
        {
            builder.HasKey(x => x.Id);

            builder.Property(x => x.TagNameSnapshot)
                .IsRequired()
                .HasMaxLength(80);

            builder.Property(x => x.TagColorSnapshot)
                .HasMaxLength(7);

            builder.Property(x => x.Confidence)
                .HasPrecision(5, 4);

            builder.Property(x => x.Reason)
                .HasMaxLength(1000);

            builder.Property(x => x.LlmModel)
                .IsRequired()
                .HasMaxLength(160);

            builder.Property(x => x.MetadataJson)
                .HasColumnType("jsonb")
                .HasDefaultValue("{}");

            builder.HasIndex(x => new { x.OrganizationId, x.MeetingId, x.Status })
                .HasDatabaseName("IX_MeetingTagSuggestions_Org_Meeting_Status");

            builder.HasIndex(x => new { x.OrganizationId, x.MeetingTagId, x.Status })
                .HasDatabaseName("IX_MeetingTagSuggestions_Org_Tag_Status");

            builder.HasIndex(x => new { x.MeetingId, x.MeetingTagId, x.Status })
                .IsUnique()
                .HasFilter("\"Status\" = 0")
                .HasDatabaseName("UX_MeetingTagSuggestions_Meeting_Tag_Pending");

            builder.HasOne(x => x.Meeting)
                .WithMany()
                .HasForeignKey(x => x.MeetingId)
                .OnDelete(DeleteBehavior.Restrict);

            builder.HasOne(x => x.MeetingTag)
                .WithMany()
                .HasForeignKey(x => x.MeetingTagId)
                .OnDelete(DeleteBehavior.Restrict);
        }
    }
}
