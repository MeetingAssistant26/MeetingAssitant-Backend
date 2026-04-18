using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MeetingAssistant.Features.Meetings.Models;

namespace MeetingAssistant.Features.Meetings.Infrastructure.Persistence.Configurations
{
    public class MeetingMeetingTagConfiguration : IEntityTypeConfiguration<MeetingMeetingTag>
    {
        public void Configure(EntityTypeBuilder<MeetingMeetingTag> builder)
        {
            builder.HasKey(mt => new { mt.MeetingId, mt.MeetingTagId });

            builder.HasOne(mt => mt.Meeting)
                .WithMany(m => m.Tags)
                .HasForeignKey(mt => mt.MeetingId)
                .OnDelete(DeleteBehavior.Restrict);

            builder.HasOne(mt => mt.MeetingTag)
                .WithMany()
                .HasForeignKey(mt => mt.MeetingTagId)
                .OnDelete(DeleteBehavior.Restrict);
        }
    }
}