using MeetingAssistant.Features.Organizations.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MeetingAssistant.Infrastructure.Persistence.EntitiesConfigrations.Organizations
{
    public class UserOrgMembershipConfiguration : IEntityTypeConfiguration<UserOrgMembership>
    {
        public void Configure(EntityTypeBuilder<UserOrgMembership> builder)
        {
            builder.HasKey(m => m.Id);

            builder.Property(m => m.OrgRole)
                   .IsRequired()
                   .HasConversion<int>();

            builder.Property(m => m.JobRole)
                   .HasMaxLength(100);

            builder.Property(m => m.Context)
                   .HasMaxLength(2000);

            builder.Property(m => m.ContextStatus)
                   .HasConversion<int?>();

            builder.Property(m => m.IsEnabled)
                   .IsRequired()
                   .HasDefaultValue(true);

            // Partial unique index: only one active membership per user
            builder.HasIndex(m => m.UserId)
                   .IsUnique()
                   .HasFilter("\"IsEnabled\" = true")
                   .HasDatabaseName("IX_UserOrgMemberships_UserId_ActiveOnly");

            builder.HasIndex(m => m.OrganizationId);

            builder.HasOne(m => m.User)
                   .WithMany()
                   .HasForeignKey(m => m.UserId);

            builder.HasOne(m => m.Organization)
                   .WithMany(o => o.Memberships)
                   .HasForeignKey(m => m.OrganizationId);
        }
    }
}
