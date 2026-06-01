using System.Security.Cryptography;
using MeetingAssistant.Features.LiveSession.Infrastructure;
using Microsoft.Extensions.Options;

namespace MeetingAssistant.Infrastructure.AI
{
    public sealed class DeterministicEmbeddingService : IEmbeddingService
    {
        public DeterministicEmbeddingService(IOptions<OpenAiCompatibleOptions> openAiCompatibleOptions)
        {
            var config = openAiCompatibleOptions.Value.Embedding;
            Metadata = EmbeddingConfiguration.GetMetadata(config);

            if (Metadata.Provider != EmbeddingProviderNames.DeterministicTest)
            {
                throw new InvalidOperationException(
                    $"{EmbeddingConfiguration.SectionName}:Provider must be '{EmbeddingProviderNames.DeterministicTest}' for deterministic embeddings.");
            }
        }

        public EmbeddingMetadata Metadata { get; }

        public Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken) =>
            Task.FromResult(CreateDeterministicEmbedding(text, Metadata.Dimension));

        public Task<float[][]> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(texts);
            return Task.FromResult(texts.Select(text => CreateDeterministicEmbedding(text, Metadata.Dimension)).ToArray());
        }

        private static float[] CreateDeterministicEmbedding(string? text, int dimension)
        {
            if (dimension <= 0)
            {
                throw new InvalidOperationException($"{EmbeddingConfiguration.SectionName}:Dimension must be greater than zero.");
            }

            var embedding = new float[dimension];
            var seed = System.Text.Encoding.UTF8.GetBytes(text ?? string.Empty);
            var offset = 0;

            while (offset < dimension)
            {
                seed = SHA256.HashData(seed);
                for (var i = 0; i < seed.Length && offset < dimension; i += 4)
                {
                    var value = BitConverter.ToUInt32(seed, i) / (float)uint.MaxValue;
                    embedding[offset++] = (value * 2f) - 1f;
                }
            }

            return embedding;
        }
    }
}
