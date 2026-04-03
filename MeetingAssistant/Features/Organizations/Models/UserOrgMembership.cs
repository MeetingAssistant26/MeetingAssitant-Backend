using MeetingAssistant.Api.Shared;
using MeetingAssistant.Features.Identity.Entites;
using MeetingAssistant.Shared.Abstractions;

namespace MeetingAssistant.Features.Organizations.Models
{
    public class UserOrgMembership : BaseEntity, IHasOrganizationId
    {
        public Guid UserId { get; set; }
        public Guid OrganizationId { get; set; }
        public OrganizationRole OrgRole { get; set; } = OrganizationRole.Member;
        public string? JobRole { get; set; }
        public string? Context { get; set; }
        public ContextStatus? ContextStatus { get; set; }
        public bool IsEnabled { get; set; } = true;

        // Navigation properties
        public ApplicationUser User { get; set; } = null!;
        public Organization Organization { get; set; } = null!;
    }
}
