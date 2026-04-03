using MeetingAssistant.Features.Organizations.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MeetingAssistant.Infrastructure.Persistence.EntitiesConfigrations.Organizations
{
    public class OrganizationConfiguration : IEntityTypeConfiguration<Organization>
    {
        public void Configure(EntityTypeBuilder<Organization> builder)
        {
            builder.HasKey(o => o.Id);

            builder.Property(o => o.Name)
                   .IsRequired()
                   .HasMaxLength(200);

            builder.Property(o => o.Slug)
                   .IsRequired()
                   .HasMaxLength(100);

            builder.HasIndex(o => o.Slug)
                   .IsUnique();

            builder.HasMany(o => o.Memberships)
                   .WithOne(m => m.Organization)
                   .HasForeignKey(m => m.OrganizationId);

            builder.HasMany(o => o.Invitations)
                   .WithOne(i => i.Organization)
                   .HasForeignKey(i => i.OrganizationId);
        }
    }
}
