using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MeetingAssistant.Features.Meetings.Models;

namespace MeetingAssistant.Features.Meetings.Infrastructure.Persistence.Configurations
{
    public class MeetingParticipantConfiguration : IEntityTypeConfiguration<MeetingParticipant>
    {
        public void Configure(EntityTypeBuilder<MeetingParticipant> builder)
        {
            builder.HasKey(mp => mp.Id);

            builder.HasIndex(mp => new { mp.MeetingId, mp.UserId })
                .IsUnique()
                .HasDatabaseName("IX_MeetingParticipants_MeetingId_UserId");

            builder.HasIndex(mp => mp.UserId)
                .HasDatabaseName("IX_MeetingParticipants_UserId");

            builder.HasIndex(mp => mp.OrganizationId)
                .HasDatabaseName("IX_MeetingParticipants_OrganizationId");

            builder.HasOne(mp => mp.Meeting)
                .WithMany(m => m.Participants)
                .HasForeignKey(mp => mp.MeetingId)
                .OnDelete(DeleteBehavior.Restrict);

            builder.HasOne(mp => mp.User)
                .WithMany()
                .HasForeignKey(mp => mp.UserId)
                .OnDelete(DeleteBehavior.Restrict);
        }
    }
}