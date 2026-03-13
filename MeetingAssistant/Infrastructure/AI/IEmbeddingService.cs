namespace MeetingAssistant.Infrastructure.AI
{
    public interface IEmbeddingService
    {
        Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken);
        Task<float[][]> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken);
    }
}
