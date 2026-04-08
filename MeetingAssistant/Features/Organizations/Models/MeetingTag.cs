using MeetingAssistant.Shared.Abstractions;
using MeetingAssistant.Api.Shared;

namespace MeetingAssistant.Features.Organizations.Models
{
    public class MeetingTag : BaseEntity, IHasOrganizationId
    {
        public Guid OrganizationId { get; set; }

        public string Name { get; set; } = string.Empty;

        public string? Color { get; set; }

        public bool IsActive { get; set; } = true;
    }
}
