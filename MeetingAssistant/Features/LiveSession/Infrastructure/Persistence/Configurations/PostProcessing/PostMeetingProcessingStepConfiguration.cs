using MeetingAssistant.Features.LiveSession.Models.PostProcessing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MeetingAssistant.Features.LiveSession.Infrastructure.Persistence.Configurations.PostProcessing
{
    public class PostMeetingProcessingStepConfiguration : IEntityTypeConfiguration<PostMeetingProcessingStep>
    {
        public void Configure(EntityTypeBuilder<PostMeetingProcessingStep> builder)
        {
            builder.HasKey(x => x.Id);

            builder.Property(x => x.StepType)
                .HasConversion<int>()
                .IsRequired();

            builder.Property(x => x.Status)
                .HasConversion<int>()
                .IsRequired();

            builder.Property(x => x.RelatedHangfireJobId)
                .HasMaxLength(128)
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

            builder.HasIndex(x => new { x.RunId, x.StepType })
                .IsUnique()
                .HasDatabaseName("UX_PostMeetingProcessingSteps_Run_StepType");

            builder.HasIndex(x => new { x.OrganizationId, x.MeetingId, x.Status })
                .HasDatabaseName("IX_PostMeetingProcessingSteps_Org_Meeting_Status");

            builder.HasIndex(x => new { x.OrganizationId, x.MeetingId, x.StepType })
                .HasDatabaseName("IX_PostMeetingProcessingSteps_Org_Meeting_StepType");

            builder.HasOne(x => x.Run)
                .WithMany(x => x.Steps)
                .HasForeignKey(x => x.RunId)
                .OnDelete(DeleteBehavior.Restrict);
        }
    }
}
