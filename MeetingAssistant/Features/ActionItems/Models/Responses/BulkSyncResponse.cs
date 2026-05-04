namespace MeetingAssistant.Features.ActionItems.Models.Responses
{
    public class BulkSyncResponse
    {
        public string IntegrationStatus { get; set; } = string.Empty;
        public List<BulkSyncItemResult> Results { get; set; } = new();
    }
}
