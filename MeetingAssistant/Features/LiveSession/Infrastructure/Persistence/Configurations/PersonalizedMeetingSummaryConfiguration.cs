using MeetingAssistant.Features.LiveSession.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MeetingAssistant.Features.LiveSession.Infrastructure.Persistence.Configurations
{
    public class PersonalizedMeetingSummaryConfiguration : IEntityTypeConfiguration<PersonalizedMeetingSummary>
    {
        public void Configure(EntityTypeBuilder<PersonalizedMeetingSummary> builder)
        {
            builder.HasKey(x => x.Id);

            builder.Property(x => x.Status)
                .HasConversion<int>()
                .IsRequired();

            builder.Property(x => x.SummaryText);

            builder.Property(x => x.LlmModel);

            builder.Property(x => x.GeneratedAtUtc)
                .HasColumnType("timestamp with time zone");

            builder.Property(x => x.TargetDisplayName)
                .HasMaxLength(256);

            builder.Property(x => x.PromptName)
                .HasMaxLength(128);

            builder.Property(x => x.PromptVersion)
                .HasMaxLength(128);

            builder.Property(x => x.PersonalizationContextJson)
                .HasColumnType("jsonb");

            builder.Property(x => x.EligibilityReason)
                .HasMaxLength(128);

            builder.Property(x => x.EligibilityContextJson)
                .HasColumnType("jsonb");

            builder.HasIndex(x => new { x.MeetingId, x.UserId })
                .IsUnique()
                .HasDatabaseName("IX_PersonalizedMeetingSummaries_MeetingId_UserId");

            builder.HasIndex(x => x.MeetingParticipantId)
                .IsUnique()
                .HasDatabaseName("IX_PersonalizedMeetingSummaries_MeetingParticipantId");

            builder.HasIndex(x => x.OrganizationId)
                .HasDatabaseName("IX_PersonalizedMeetingSummaries_OrganizationId");

            builder.HasIndex(x => new { x.OrganizationId, x.MeetingId })
                .HasDatabaseName("IX_PersonalizedMeetingSummaries_OrganizationId_MeetingId");

            builder.HasIndex(x => new { x.OrganizationId, x.UserId })
                .HasDatabaseName("IX_PersonalizedMeetingSummaries_OrganizationId_UserId");

            builder.HasOne(x => x.Meeting)
                .WithMany()
                .HasForeignKey(x => x.MeetingId)
                .OnDelete(DeleteBehavior.Restrict);

            builder.HasOne(x => x.MeetingParticipant)
                .WithMany()
                .HasForeignKey(x => x.MeetingParticipantId)
                .OnDelete(DeleteBehavior.Restrict);

            builder.HasOne(x => x.User)
                .WithMany()
                .HasForeignKey(x => x.UserId)
                .OnDelete(DeleteBehavior.Restrict);
        }
    }
}
