using MeetingAssistant.Features.LiveSession.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MeetingAssistant.Features.LiveSession.Infrastructure.Persistence.Configurations
{
    public class MeetingTranscriptConfiguration : IEntityTypeConfiguration<MeetingTranscript>
    {
        public void Configure(EntityTypeBuilder<MeetingTranscript> builder)
        {
            builder.HasKey(x => x.Id);

            builder.Property(x => x.FullText)
                .IsRequired();

            builder.Property(x => x.SegmentsJson)
                .HasColumnType("jsonb")
                .IsRequired();

            builder.Property(x => x.SttModel)
                .IsRequired();

            builder.Property(x => x.GeneratedAtUtc)
                .HasColumnType("timestamp with time zone")
                .IsRequired();

            builder.HasIndex(x => x.MeetingId)
                .IsUnique()
                .HasDatabaseName("IX_MeetingTranscripts_MeetingId");

            builder.HasIndex(x => x.OrganizationId)
                .HasDatabaseName("IX_MeetingTranscripts_OrganizationId");

            builder.HasOne(x => x.Meeting)
                .WithMany()
                .HasForeignKey(x => x.MeetingId)
                .OnDelete(DeleteBehavior.Restrict);
        }
    }
}
