using MeetingAssistant.Features.ActionItems.Models.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MeetingAssistant.Infrastructure.Persistence.EntitiesConfigrations.ActionItems
{
    public class OrganizationIntegrationConfiguration : IEntityTypeConfiguration<OrganizationIntegration>
    {
        public void Configure(EntityTypeBuilder<OrganizationIntegration> builder)
        {
            builder.Property(x => x.Type)
                .IsRequired();

            builder.Property(x => x.Status)
                .IsRequired();

            builder.HasIndex(x => new { x.OrganizationId, x.Type })
                .IsUnique()
                .HasDatabaseName("IX_OrganizationIntegrations_OrgId_Type");
        }
    }
}
