using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MeetingAssistant.Features.Organizations.Models;

namespace MeetingAssistant.Features.Organizations.Infrastructure.Persistence.Configurations
{
    public class OrganizationConfiguration : IEntityTypeConfiguration<Organization>
    {
        public void Configure(EntityTypeBuilder<Organization> builder)
        {
            builder.ToTable("Organizations");

            builder.Property(o => o.Name).IsRequired().HasMaxLength(150);
            builder.Property(o => o.Slug).IsRequired().HasMaxLength(100);

            builder.HasIndex(o => o.Slug).IsUnique();
        }
    }
}
