using MeetingAssistant.Features.ActionItems.Models.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MeetingAssistant.Infrastructure.Persistence.EntitiesConfigrations.ActionItems
{
    public class ExternalAccountLinkConfiguration : IEntityTypeConfiguration<ExternalAccountLink>
    {
        public void Configure(EntityTypeBuilder<ExternalAccountLink> builder)
        {
            builder.Property(x => x.Provider)
                .IsRequired();

            builder.Property(x => x.ExternalUserId)
                .HasMaxLength(50)
                .IsRequired();

            builder.Property(x => x.ExternalUsername)
                .HasMaxLength(100);

            builder.Property(x => x.AccessTokenProtected)
                .HasMaxLength(500)
                .IsRequired();

            builder.HasIndex(x => new { x.UserId, x.OrganizationId, x.Provider })
                .IsUnique()
                .HasDatabaseName("IX_ExternalAccountLinks_UserId_OrgId_Provider");

            builder.HasOne(x => x.User)
                .WithMany()
                .HasForeignKey(x => x.UserId)
                .OnDelete(DeleteBehavior.Restrict);
        }
    }
}
