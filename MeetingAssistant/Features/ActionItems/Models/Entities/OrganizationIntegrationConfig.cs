using MeetingAssistant.Api.Shared;
using MeetingAssistant.Features.ActionItems.Models.Enums;
using MeetingAssistant.Shared.Abstractions;

namespace MeetingAssistant.Features.ActionItems.Models.Entities
{
    public class OrganizationIntegrationConfig : BaseEntity, IHasOrganizationId
    {
        public Guid OrganizationId { get; set; }

        public ExternalProvider Provider { get; set; }

        public string SelectedProjectId { get; set; } = string.Empty;

        public string SelectedListId { get; set; } = string.Empty;

        public string EncryptedProviderPayload { get; set; } = string.Empty;
    }
}
