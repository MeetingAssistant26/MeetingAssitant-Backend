namespace MeetingAssistant.Features.Rag.Services
{
    public interface IReindexMeetingKnowledgeService
    {
        Task<ReindexMeetingKnowledgeResult> ReindexMeetingAsync(
            Guid organizationId,
            Guid meetingId,
            CancellationToken cancellationToken);
    }
}
