namespace MeetingAssistant.Features.ActionItems.Models.Responses
{
    public class ActionItemResponse
    {
        public Guid Id { get; set; }
        public Guid MeetingId { get; set; }
        public string Title { get; set; } = string.Empty;
        public string? Description { get; set; }
        public Guid? AssignedToUserId { get; set; }
        public string? AssignedToUserName { get; set; }
        public DateTime? DueDateUtc { get; set; }
        public string Status { get; set; } = string.Empty;
        public string? ExternalTaskId { get; set; }
        public string? ExternalTaskUrl { get; set; }
        public string? ExternalProvider { get; set; }
        public string? SyncMissingAssigneeReason { get; set; }
        public DateTime ExtractedAtUtc { get; set; }
        public DateTime? SyncedAtUtc { get; set; }
        public string? RowVersionEtag { get; set; }
    }
}
