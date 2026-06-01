using MeetingAssistant.Features.LiveSession.Models.PostProcessing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MeetingAssistant.Features.LiveSession.Infrastructure.Persistence.Configurations.PostProcessing
{
    public class PostMeetingProcessingRunConfiguration : IEntityTypeConfiguration<PostMeetingProcessingRun>
    {
        public void Configure(EntityTypeBuilder<PostMeetingProcessingRun> builder)
        {
            builder.HasKey(x => x.Id);

            builder.Property(x => x.Status)
                .HasConversion<int>()
                .IsRequired();

            builder.Property(x => x.RelatedHangfireJobId)
                .HasMaxLength(128)
                .IsRequired(false);

            builder.Property(x => x.ErrorCode)
                .HasMaxLength(128)
                .IsRequired(false);

            builder.Property(x => x.ErrorMessage)
                .HasMaxLength(2000)
                .IsRequired(false);

            builder.HasIndex(x => new { x.OrganizationId, x.MeetingId, x.PipelineGenerationId })
                .IsUnique()
                .HasDatabaseName("UX_PostMeetingProcessingRuns_Org_Meeting_Generation");

            builder.HasIndex(x => new { x.OrganizationId, x.MeetingId, x.CreatedAtUtc })
                .HasDatabaseName("IX_PostMeetingProcessingRuns_Org_Meeting_CreatedAt");

            builder.HasIndex(x => new { x.OrganizationId, x.Status })
                .HasDatabaseName("IX_PostMeetingProcessingRuns_Org_Status");

            builder.HasOne(x => x.Meeting)
                .WithMany()
                .HasForeignKey(x => x.MeetingId)
                .OnDelete(DeleteBehavior.Restrict);
        }
    }
}
