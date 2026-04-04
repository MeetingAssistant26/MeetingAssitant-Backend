using MeetingAssistant.Api.Shared;
using MeetingAssistant.Features.Identity.Entites;
using MeetingAssistant.Shared.Abstractions;

namespace MeetingAssistant.Features.Organizations.Models
{
    public class Invitation : BaseEntity, IHasOrganizationId
    {
        public Guid OrganizationId { get; set; }
        public Guid InvitedByUserId { get; set; }
        public string Token { get; set; } = string.Empty;
        public List<string> EmailWhitelist { get; set; } = new();
        public DateTime ExpiresAtUtc { get; set; }
        public DateTime? RevokedAtUtc { get; set; }
        public Guid? RevokedByUserId { get; set; }

        // Navigation properties
        public Organization Organization { get; set; } = null!;
        public ApplicationUser InvitedByUser { get; set; } = null!;
        public ApplicationUser? RevokedByUser { get; set; }
    }
}
