namespace MeetingAssistant.Features.Rag.Services
{
    public interface IKnowledgeRetrievalService
    {
        Task<IReadOnlyList<KnowledgeRetrievalResult>> RetrieveAsync(
            KnowledgeRetrievalRequest request,
            CancellationToken cancellationToken);
    }
}
