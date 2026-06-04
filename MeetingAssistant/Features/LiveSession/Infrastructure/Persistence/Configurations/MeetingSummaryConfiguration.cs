using MeetingAssistant.Features.LiveSession.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MeetingAssistant.Features.LiveSession.Infrastructure.Persistence.Configurations
{
    public class MeetingSummaryConfiguration : IEntityTypeConfiguration<MeetingSummary>
    {
        public void Configure(EntityTypeBuilder<MeetingSummary> builder)
        {
            builder.HasKey(x => x.Id);

            builder.Property(x => x.SummaryText)
                .IsRequired();

            builder.Property(x => x.LlmModel)
                .IsRequired();

            builder.Property(x => x.GeneratedAtUtc)
                .HasColumnType("timestamp with time zone")
                .IsRequired();

            builder.Property(x => x.SourceTranscriptHash)
                .HasMaxLength(64);

            builder.HasIndex(x => x.MeetingId)
                .IsUnique()
                .HasDatabaseName("IX_MeetingSummaries_MeetingId");

            builder.HasIndex(x => x.OrganizationId)
                .HasDatabaseName("IX_MeetingSummaries_OrganizationId");

            builder.HasOne(x => x.Meeting)
                .WithMany()
                .HasForeignKey(x => x.MeetingId)
                .OnDelete(DeleteBehavior.Restrict);
        }
    }
}
