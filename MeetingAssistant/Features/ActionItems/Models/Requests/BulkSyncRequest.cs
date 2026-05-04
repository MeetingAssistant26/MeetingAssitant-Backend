namespace MeetingAssistant.Features.ActionItems.Models.Requests
{
    public class BulkSyncRequest
    {
        public List<Guid> ActionItemIds { get; set; } = new();
    }
}
