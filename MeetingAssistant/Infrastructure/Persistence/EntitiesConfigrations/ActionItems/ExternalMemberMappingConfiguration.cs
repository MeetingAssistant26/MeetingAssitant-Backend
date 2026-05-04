using MeetingAssistant.Features.ActionItems.Models.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MeetingAssistant.Infrastructure.Persistence.EntitiesConfigrations.ActionItems
{
    public class ExternalMemberMappingConfiguration : IEntityTypeConfiguration<ExternalMemberMapping>
    {
        public void Configure(EntityTypeBuilder<ExternalMemberMapping> builder)
        {
            builder.Property(x => x.Provider)
                .IsRequired();

            builder.Property(x => x.ExternalMemberId)
                .HasMaxLength(50)
                .IsRequired();

            builder.HasIndex(x => new { x.OrganizationId, x.UserId, x.Provider })
                .IsUnique()
                .HasDatabaseName("IX_ExternalMemberMappings_OrgId_UserId_Provider");

            builder.HasOne(x => x.User)
                .WithMany()
                .HasForeignKey(x => x.UserId)
                .OnDelete(DeleteBehavior.Restrict);
        }
    }
}
