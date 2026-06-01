using MeetingAssistant.Features.LiveSession.Models.PostProcessing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MeetingAssistant.Features.LiveSession.Infrastructure.Persistence.Configurations.PostProcessing
{
    public class PostMeetingProcessingEventConfiguration : IEntityTypeConfiguration<PostMeetingProcessingEvent>
    {
        public void Configure(EntityTypeBuilder<PostMeetingProcessingEvent> builder)
        {
            builder.HasKey(x => x.Id);

            builder.Property(x => x.StepType)
                .HasConversion<int>()
                .IsRequired(false);

            builder.Property(x => x.EventType)
                .HasConversion<int>()
                .IsRequired();

            builder.Property(x => x.Status)
                .HasConversion<int>()
                .IsRequired(false);

            builder.Property(x => x.RelatedHangfireJobId)
                .HasMaxLength(128)
                .IsRequired(false);

            builder.Property(x => x.Message)
                .HasMaxLength(2000)
                .IsRequired(false);

            builder.Property(x => x.ArtifactType)
                .HasMaxLength(128)
                .IsRequired(false);

            builder.Property(x => x.ArtifactIdsJson)
                .HasColumnType("jsonb")
                .IsRequired(false);

            builder.Property(x => x.ErrorCode)
                .HasMaxLength(128)
                .IsRequired(false);

            builder.Property(x => x.ErrorMessage)
                .HasMaxLength(2000)
                .IsRequired(false);

            builder.Property(x => x.MetadataJson)
                .HasColumnType("jsonb")
                .IsRequired(false);

            builder.HasIndex(x => new { x.RunId, x.OccurredAtUtc })
                .HasDatabaseName("IX_PostMeetingProcessingEvents_Run_OccurredAt");

            builder.HasIndex(x => new { x.OrganizationId, x.MeetingId, x.OccurredAtUtc })
                .HasDatabaseName("IX_PostMeetingProcessingEvents_Org_Meeting_OccurredAt");

            builder.HasIndex(x => x.StepId)
                .HasDatabaseName("IX_PostMeetingProcessingEvents_StepId");

            builder.HasOne(x => x.Run)
                .WithMany(x => x.Events)
                .HasForeignKey(x => x.RunId)
                .OnDelete(DeleteBehavior.Restrict);

            builder.HasOne(x => x.Step)
                .WithMany(x => x.Events)
                .HasForeignKey(x => x.StepId)
                .OnDelete(DeleteBehavior.Restrict);
        }
    }
}
