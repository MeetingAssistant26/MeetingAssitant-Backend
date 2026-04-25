using MeetingAssistant.Features.Identity.Entites;
using MeetingAssistant.Features.LiveSession.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MeetingAssistant.Features.LiveSession.Infrastructure.Persistence.Configurations
{
    public class SessionEventConfiguration : IEntityTypeConfiguration<SessionEvent>
    {
        public void Configure(EntityTypeBuilder<SessionEvent> builder)
        {
            builder.HasKey(x => x.Id);

            builder.Property(x => x.ExternalEventId)
                .HasMaxLength(100)
                .IsRequired();

            builder.Property(x => x.PayloadJson)
                .HasColumnType("jsonb")
                .IsRequired();

            builder.Property(x => x.OccurredAtUtc)
                .HasColumnType("timestamp with time zone")
                .IsRequired();

            builder.Property(x => x.ProcessedAtUtc)
                .HasColumnType("timestamp with time zone")
                .IsRequired();

            builder.HasIndex(x => x.ExternalEventId)
                .IsUnique()
                .HasDatabaseName("IX_SessionEvents_ExternalEventId");

            builder.HasIndex(x => new { x.MeetingId, x.OccurredAtUtc })
                .HasDatabaseName("IX_SessionEvents_MeetingId_OccurredAtUtc");

            builder.HasIndex(x => new { x.MeetingId, x.EventType })
                .IsUnique()
                .HasDatabaseName("IX_SessionEvents_MeetingId_EventType");

            builder.HasIndex(x => x.OrganizationId)
                .HasDatabaseName("IX_SessionEvents_OrganizationId");

            builder.HasOne(x => x.Meeting)
                .WithMany()
                .HasForeignKey(x => x.MeetingId)
                .OnDelete(DeleteBehavior.Restrict);

            builder.HasOne<ApplicationUser>()
                .WithMany()
                .HasForeignKey(x => x.ParticipantUserId)
                .OnDelete(DeleteBehavior.Restrict);
        }
    }
}
