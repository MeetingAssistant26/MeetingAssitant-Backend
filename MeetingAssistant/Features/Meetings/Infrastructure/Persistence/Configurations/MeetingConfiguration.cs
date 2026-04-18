using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MeetingAssistant.Features.Meetings.Models;

namespace MeetingAssistant.Features.Meetings.Infrastructure.Persistence.Configurations
{
    public class MeetingConfiguration : IEntityTypeConfiguration<Meeting>
    {
        public void Configure(EntityTypeBuilder<Meeting> builder)
        {
            builder.HasKey(m => m.Id);

            builder.Property(m => m.Title)
                .IsRequired()
                .HasMaxLength(200);

            builder.Property(m => m.Description)
                .HasMaxLength(2000);

            builder.HasIndex(m => m.OrganizationId)
                .HasDatabaseName("IX_Meetings_OrganizationId");

            builder.HasIndex(m => new { m.OrganizationId, m.ScheduledStartUtc })
                .HasDatabaseName("IX_Meetings_OrgId_ScheduledStartUtc");

            builder.HasIndex(m => new { m.OrganizationId, m.Status })
                .HasDatabaseName("IX_Meetings_OrgId_Status");

            builder.OwnsOne(m => m.RecurrenceConfig, r =>
            {
                r.ToJson();
            });
        }
    }
}