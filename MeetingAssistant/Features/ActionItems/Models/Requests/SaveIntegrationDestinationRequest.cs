namespace MeetingAssistant.Features.ActionItems.Models.Requests
{
    public class SaveIntegrationDestinationRequest
    {
        public string WorkspaceId { get; set; } = string.Empty;
        public string BoardId { get; set; } = string.Empty;
        public string ListId { get; set; } = string.Empty;
    }
}
