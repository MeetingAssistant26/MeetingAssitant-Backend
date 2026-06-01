namespace MeetingAssistant.Features.ActionItems.Models.Requests
{
    public class UpdateActionItemRequest
    {
        public string? Title { get; set; }
        public string? Description { get; set; }
        public Guid? AssignedToParticipantId { get; set; }
        public DateTime? DueDateUtc { get; set; }
        public bool ClearDueDateReview { get; set; }
        public string? Status { get; set; }
    }
}
