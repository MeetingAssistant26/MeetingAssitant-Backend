using MeetingAssistant.Api.Shared;

namespace MeetingAssistant.Features.Organizations.Models
{
    public class Organization : BaseEntity
    {
        public string Name { get; set; } = string.Empty;
        public string Slug { get; set; } = string.Empty;

        // Navigation properties
        public ICollection<UserOrgMembership> Memberships { get; set; } = new List<UserOrgMembership>();
        public ICollection<Invitation> Invitations { get; set; } = new List<Invitation>();
    }
}
