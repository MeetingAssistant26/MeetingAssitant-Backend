using MeetingAssistant.Features.ActionItems.Models.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MeetingAssistant.Infrastructure.Persistence.EntitiesConfigrations.ActionItems
{
    public class ActionItemConfiguration : IEntityTypeConfiguration<ActionItem>
    {
        public void Configure(EntityTypeBuilder<ActionItem> builder)
        {
            builder.Property(x => x.Title)
                .HasMaxLength(200)
                .IsRequired();

            builder.Property(x => x.Description)
                .HasMaxLength(2000);

            builder.Property(x => x.Status)
                .IsRequired();

            builder.Property(x => x.ExternalTaskId)
                .HasMaxLength(50);

            builder.Property(x => x.ExternalTaskUrl)
                .HasMaxLength(500);

            builder.Property(x => x.SyncMissingAssigneeReason)
                .HasMaxLength(50);

            builder.Property(x => x.RowVersion)
                .IsRowVersion();

            builder.HasIndex(x => new { x.MeetingId, x.Status })
                .HasDatabaseName("IX_ActionItems_MeetingId_Status");

            builder.HasIndex(x => new { x.OrganizationId, x.Status })
                .HasDatabaseName("IX_ActionItems_OrganizationId_Status");

            builder.HasIndex(x => new { x.AssignedToUserId, x.Status })
                .HasDatabaseName("IX_ActionItems_AssignedToUserId_Status");

            builder.HasOne(x => x.Meeting)
                .WithMany()
                .HasForeignKey(x => x.MeetingId)
                .OnDelete(DeleteBehavior.Restrict);

            builder.HasOne(x => x.AssignedToParticipant)
                .WithMany()
                .HasForeignKey(x => x.AssignedToParticipantId)
                .OnDelete(DeleteBehavior.Restrict);

            builder.HasOne(x => x.AssignedToUser)
                .WithMany()
                .HasForeignKey(x => x.AssignedToUserId)
                .OnDelete(DeleteBehavior.Restrict);
        }
    }
}
