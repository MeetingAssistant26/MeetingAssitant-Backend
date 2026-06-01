using MeetingAssistant.Features.Meetings.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MeetingAssistant.Features.Meetings.Infrastructure.Persistence.Configurations
{
    public sealed class RecurringMeetingSeriesConfiguration : IEntityTypeConfiguration<RecurringMeetingSeries>
    {
        public void Configure(EntityTypeBuilder<RecurringMeetingSeries> builder)
        {
            builder.HasKey(s => s.Id);

            builder.Property(s => s.Title)
                .IsRequired()
                .HasMaxLength(200);

            builder.Property(s => s.Description)
                .HasMaxLength(2000);

            builder.Property(s => s.DaysOfWeek)
                .HasMaxLength(100);

            builder.HasIndex(s => new { s.OrganizationId, s.Status })
                .HasDatabaseName("IX_RecurringMeetingSeries_OrganizationId_Status");
        }
    }
}
