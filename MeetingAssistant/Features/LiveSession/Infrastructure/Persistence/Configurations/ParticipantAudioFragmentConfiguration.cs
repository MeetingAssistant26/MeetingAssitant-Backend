using MeetingAssistant.Features.LiveSession.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MeetingAssistant.Features.LiveSession.Infrastructure.Persistence.Configurations
{
    public class ParticipantAudioFragmentConfiguration : IEntityTypeConfiguration<ParticipantAudioFragment>
    {
        public void Configure(EntityTypeBuilder<ParticipantAudioFragment> builder)
        {
            builder.HasKey(x => x.Id);

            builder.Property(x => x.TrackSid)
                .HasMaxLength(128)
                .IsRequired();

            builder.Property(x => x.EgressId)
                .HasMaxLength(128)
                .IsRequired(false);

            builder.Property(x => x.StorageObjectKey)
                .HasMaxLength(500)
                .IsRequired(false);

            builder.Property(x => x.StorageLocation)
                .HasMaxLength(1000)
                .IsRequired(false);

            builder.Property(x => x.BackupStoragePath)
                .HasMaxLength(1000)
                .IsRequired(false);

            builder.Property(x => x.Status)
                .HasConversion<int>()
                .IsRequired();

            builder.Property(x => x.EgressStartAttemptCount)
                .HasDefaultValue(0)
                .IsRequired();

            builder.Property(x => x.StorageUploadAttemptCount)
                .HasDefaultValue(0)
                .IsRequired();

            builder.Property(x => x.FailureCode)
                .HasMaxLength(128)
                .IsRequired(false);

            builder.Property(x => x.FailureMessage)
                .HasMaxLength(2000)
                .IsRequired(false);

            builder.HasIndex(x => new { x.OrganizationId, x.MeetingId, x.ParticipantUserId })
                .HasDatabaseName("IX_ParticipantAudioFragments_Org_Meeting_Participant");

            builder.HasIndex(x => new { x.MeetingId, x.Status })
                .HasDatabaseName("IX_ParticipantAudioFragments_MeetingId_Status");

            builder.HasIndex(x => new { x.MeetingId, x.ParticipantUserId, x.TrackPublishedAtUtc })
                .HasDatabaseName("IX_ParticipantAudioFragments_Meeting_Participant_PublishedAt");

            builder.HasIndex(x => x.TrackSid)
                .HasDatabaseName("IX_ParticipantAudioFragments_TrackSid");

            builder.HasIndex(x => new { x.MeetingId, x.TrackSid })
                .IsUnique()
                .HasDatabaseName("UX_ParticipantAudioFragments_MeetingId_TrackSid");

            builder.HasIndex(x => x.EgressId)
                .HasDatabaseName("IX_ParticipantAudioFragments_EgressId");

            builder.HasIndex(x => new { x.Status, x.EgressId, x.EgressStartLeaseExpiresAtUtc })
                .HasDatabaseName("IX_ParticipantAudioFragments_EgressStartLease");

            builder.HasIndex(x => new { x.Status, x.BackupStoragePath, x.StorageUploadLeaseExpiresAtUtc })
                .HasDatabaseName("IX_ParticipantAudioFragments_StorageUploadLease");

            builder.HasIndex(x => x.ParticipantAudioTrackId)
                .HasDatabaseName("IX_ParticipantAudioFragments_ParticipantAudioTrackId");

            builder.HasOne(x => x.Meeting)
                .WithMany()
                .HasForeignKey(x => x.MeetingId)
                .OnDelete(DeleteBehavior.Restrict);

            builder.HasOne(x => x.ParticipantAudioTrack)
                .WithMany(x => x.Fragments)
                .HasForeignKey(x => x.ParticipantAudioTrackId)
                .OnDelete(DeleteBehavior.SetNull);
        }
    }
}
