using MeetingAssistant.Features.ActionItems.Models.Enums;

namespace MeetingAssistant.Features.ActionItems.Models.Requests
{
    public class SaveIntegrationConfigRequest
    {
        public string ApiKey { get; set; } = string.Empty;
        public string ApiToken { get; set; } = string.Empty;
        public string ProjectId { get; set; } = string.Empty;
        public string ListId { get; set; } = string.Empty;
    }
}
