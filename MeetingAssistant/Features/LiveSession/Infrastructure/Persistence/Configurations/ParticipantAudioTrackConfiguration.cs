using MeetingAssistant.Features.LiveSession.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MeetingAssistant.Features.LiveSession.Infrastructure.Persistence.Configurations
{
    public class ParticipantAudioTrackConfiguration : IEntityTypeConfiguration<ParticipantAudioTrack>
    {
        public void Configure(EntityTypeBuilder<ParticipantAudioTrack> builder)
        {
            builder.HasKey(x => x.Id);

            builder.Property(x => x.StorageObjectKey)
                .HasMaxLength(500)
                .IsRequired(false);

            builder.Property(x => x.Status)
                .HasConversion<int>()
                .IsRequired();

            builder.HasIndex(x => new { x.MeetingId, x.ParticipantUserId })
                .IsUnique()
                .HasDatabaseName("IX_ParticipantAudioTracks_MeetingId_ParticipantUserId");

            builder.HasIndex(x => new { x.MeetingId, x.Status })
                .HasDatabaseName("IX_ParticipantAudioTracks_MeetingId_Status");

            builder.HasIndex(x => x.OrganizationId)
                .HasDatabaseName("IX_ParticipantAudioTracks_OrganizationId");

            builder.HasOne(x => x.Meeting)
                .WithMany()
                .HasForeignKey(x => x.MeetingId)
                .OnDelete(DeleteBehavior.Restrict);
        }
    }
}
