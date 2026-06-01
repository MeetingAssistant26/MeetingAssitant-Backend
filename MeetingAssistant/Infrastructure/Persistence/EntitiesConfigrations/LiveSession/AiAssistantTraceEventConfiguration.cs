using MeetingAssistant.Features.LiveSession.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MeetingAssistant.Infrastructure.Persistence.EntitiesConfigrations.LiveSession
{
    public class AiAssistantTraceEventConfiguration : IEntityTypeConfiguration<AiAssistantTraceEvent>
    {
        public void Configure(EntityTypeBuilder<AiAssistantTraceEvent> builder)
        {
            builder.Property(x => x.SessionId)
                .HasMaxLength(128)
                .IsRequired();

            builder.Property(x => x.TurnId)
                .HasMaxLength(128)
                .IsRequired();

            builder.Property(x => x.EventType)
                .HasMaxLength(64)
                .IsRequired();

            builder.Property(x => x.ParticipantIdentity)
                .HasMaxLength(256);

            builder.Property(x => x.State)
                .HasMaxLength(128);

            builder.Property(x => x.StepType)
                .HasMaxLength(32);

            builder.Property(x => x.StepProvider)
                .HasMaxLength(128);

            builder.Property(x => x.StepEndpoint)
                .HasMaxLength(512);

            builder.Property(x => x.StepModel)
                .HasMaxLength(128);

            builder.Property(x => x.StepVoice)
                .HasMaxLength(128);

            builder.Property(x => x.ErrorType)
                .HasMaxLength(128);

            builder.Property(x => x.RequestPayloadJson)
                .HasColumnType("jsonb");

            builder.Property(x => x.ResponsePayloadJson)
                .HasColumnType("jsonb");

            builder.HasIndex(x => x.OrganizationId)
                .HasDatabaseName("IX_AiAssistantTraceEvents_OrganizationId");

            builder.HasIndex(x => new { x.OrganizationId, x.MeetingId, x.OccurredAtUtc })
                .HasDatabaseName("IX_AiAssistantTraceEvents_Org_Meeting_OccurredAtUtc");

            builder.HasIndex(x => new { x.OrganizationId, x.MeetingId, x.SessionId, x.TurnId, x.Sequence })
                .HasDatabaseName("IX_AiAssistantTraceEvents_Turn_Sequence");

            builder.HasOne(x => x.Meeting)
                .WithMany()
                .HasForeignKey(x => x.MeetingId)
                .OnDelete(DeleteBehavior.Restrict);
        }
    }
}
