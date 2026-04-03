using MeetingAssistant.Features.Organizations.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MeetingAssistant.Infrastructure.Persistence.EntitiesConfigrations.Organizations
{
    public class InvitationConfiguration : IEntityTypeConfiguration<Invitation>
    {
        public void Configure(EntityTypeBuilder<Invitation> builder)
        {
            builder.HasKey(i => i.Id);

            builder.Property(i => i.Token)
                   .IsRequired()
                   .HasMaxLength(256);

            builder.HasIndex(i => i.Token)
                   .IsUnique();

            builder.Property(i => i.EmailWhitelist)
                   .HasColumnType("jsonb");

            builder.Property(i => i.ExpiresAtUtc)
                   .IsRequired();

            builder.HasOne(i => i.Organization)
                   .WithMany(o => o.Invitations)
                   .HasForeignKey(i => i.OrganizationId);

            builder.HasOne(i => i.InvitedByUser)
                   .WithMany()
                   .HasForeignKey(i => i.InvitedByUserId);

            builder.HasOne(i => i.RevokedByUser)
                   .WithMany()
                   .HasForeignKey(i => i.RevokedByUserId)
                   .IsRequired(false);
        }
    }
}
