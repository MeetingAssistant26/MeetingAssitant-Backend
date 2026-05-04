using MeetingAssistant.Api.Shared;
using MeetingAssistant.Features.ActionItems.Models.Enums;
using MeetingAssistant.Shared.Abstractions;

namespace MeetingAssistant.Features.ActionItems.Models.Entities
{
    public class OrganizationIntegration : BaseEntity, IHasOrganizationId
    {
        public Guid OrganizationId { get; set; }

        public ExternalProvider Type { get; set; }

        public IntegrationStatus Status { get; set; } = IntegrationStatus.Disabled;
    }
}
