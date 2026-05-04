using MeetingAssistant.Features.ActionItems.Models.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MeetingAssistant.Infrastructure.Persistence.EntitiesConfigrations.ActionItems
{
    public class OrganizationIntegrationConfigConfiguration : IEntityTypeConfiguration<OrganizationIntegrationConfig>
    {
        public void Configure(EntityTypeBuilder<OrganizationIntegrationConfig> builder)
        {
            builder.Property(x => x.Provider)
                .IsRequired();

            builder.Property(x => x.SelectedProjectId)
                .HasMaxLength(50)
                .IsRequired();

            builder.Property(x => x.SelectedListId)
                .HasMaxLength(50)
                .IsRequired();

            builder.Property(x => x.EncryptedProviderPayload)
                .HasMaxLength(2000)
                .IsRequired();

            builder.HasIndex(x => new { x.OrganizationId, x.Provider })
                .IsUnique()
                .HasDatabaseName("IX_OrganizationIntegrationConfigs_OrgId_Provider");
        }
    }
}
