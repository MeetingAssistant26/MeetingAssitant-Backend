using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MeetingAssistant.Features.Tasks.Models.Entities;

namespace MeetingAssistant.Infrastructure.Persistence.Configurations
{
    public class ReminderConfiguration : IEntityTypeConfiguration<Reminder>
    {
        public void Configure(EntityTypeBuilder<Reminder> builder)
        {
            builder.ToTable("Reminders");

            builder.HasKey(r => r.Id);

            builder.Property(r => r.Text)
                .IsRequired()
                .HasMaxLength(500);

            builder.Property(r => r.Scope)
                .HasConversion<string>()
                .HasMaxLength(20);

            builder.Property(r => r.Channel)
                .HasConversion<string>()
                .HasMaxLength(20);

            builder.Property(r => r.Status)
                .HasConversion<string>()
                .HasMaxLength(20);

            builder.Property(r => r.OriginalText)
                .HasMaxLength(500);

            builder.HasIndex(r => new { r.TargetUserId, r.Status })
                .HasDatabaseName("IX_Reminders_TargetUserId_Status");

            builder.HasIndex(r => new { r.MeetingId, r.Scope, r.Status })
                .HasDatabaseName("IX_Reminders_MeetingId_Scope_Status");

            builder.HasIndex(r => new { r.OrganizationId, r.Status, r.ReminderAtUtc })
                .HasDatabaseName("IX_Reminders_Org_Status_ReminderAtUtc");
        }
    }
}
