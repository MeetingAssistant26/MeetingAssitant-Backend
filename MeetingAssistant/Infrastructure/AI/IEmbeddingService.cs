namespace MeetingAssistant.Infrastructure.AI
{
    public interface IEmbeddingService
    {
        EmbeddingMetadata Metadata { get; }

        Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken);

        Task<float[][]> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken);
    }
}
