namespace MeetingAssistant.Features.ActionItems.Models.Responses
{
    public class SyncResultResponse
    {
        public Guid ActionItemId { get; set; }
        public string Status { get; set; } = string.Empty;
        public string? ExternalTaskId { get; set; }
        public string? ExternalTaskUrl { get; set; }
        public string? SyncMissingAssigneeReason { get; set; }
        public string? RowVersionEtag { get; set; }
    }
}
