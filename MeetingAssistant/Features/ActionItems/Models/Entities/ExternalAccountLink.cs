using MeetingAssistant.Api.Shared;
using MeetingAssistant.Features.ActionItems.Models.Enums;
using MeetingAssistant.Features.Identity.Entites;
using MeetingAssistant.Shared.Abstractions;

namespace MeetingAssistant.Features.ActionItems.Models.Entities
{
    public class ExternalAccountLink : BaseEntity, IHasOrganizationId
    {
        public Guid OrganizationId { get; set; }

        public Guid UserId { get; set; }
        public ApplicationUser User { get; set; } = default!;

        public ExternalProvider Provider { get; set; }

        public string ExternalUserId { get; set; } = string.Empty;

        public string? ExternalUsername { get; set; }

        public string AccessTokenProtected { get; set; } = string.Empty;
    }
}
