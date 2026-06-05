using System.Net.Http.Json;
using MeetingAssistant.Features.LiveSession.Infrastructure;
using MeetingAssistant.Infrastructure.AI.DTOs;
using Microsoft.Extensions.Options;

namespace MeetingAssistant.Infrastructure.AI
{
    public sealed class OpenAiCompatibleEmbeddingService : IEmbeddingService
    {
        private readonly HttpClient _httpClient;
        private readonly Uri _endpoint;

        public OpenAiCompatibleEmbeddingService(
            HttpClient httpClient,
            IOptions<OpenAiCompatibleOptions> openAiCompatibleOptions)
        {
            var config = openAiCompatibleOptions.Value.Embedding;
            Metadata = EmbeddingConfiguration.GetMetadata(config);

            if (Metadata.Provider != EmbeddingProviderNames.OpenAiCompatible)
            {
                throw new InvalidOperationException(
                    $"{EmbeddingConfiguration.SectionName}:Provider must be '{EmbeddingProviderNames.OpenAiCompatible}' for OpenAI-compatible embeddings.");
            }

            _httpClient = httpClient;
            _endpoint = EmbeddingConfiguration.GetOpenAiCompatibleEndpoint(config);
        }

        public EmbeddingMetadata Metadata { get; }

        public async Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken)
        {
            var embeddings = await EmbedBatchPayloadAsync(text, expectedCount: 1, cancellationToken);
            return embeddings[0];
        }

        public Task<float[][]> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(texts);
            return EmbedBatchPayloadAsync(texts, texts.Count, cancellationToken);
        }

        private async Task<float[][]> EmbedBatchPayloadAsync(
            object input,
            int expectedCount,
            CancellationToken cancellationToken)
        {
            var request = new EmbeddingRequest
            {
                Model = Metadata.Model,
                Input = input,
                Dimensions = Metadata.Dimension
            };

            var response = await _httpClient.PostAsJsonAsync(_endpoint, request, cancellationToken);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<EmbeddingResponse>(cancellationToken)
                ?? throw new InvalidOperationException("Embedding API returned null response.");

            var embeddings = result.Data?
                .OrderBy(d => d.Index)
                .Select(d => d.Embedding ?? throw new InvalidOperationException($"Embedding at index {d.Index} was null."))
                .ToArray()
                ?? throw new InvalidOperationException("Embedding API returned no embedding data.");

            if (embeddings.Length != expectedCount)
            {
                throw new InvalidOperationException(
                    $"Embedding API returned {embeddings.Length} embedding(s), but {expectedCount} were requested.");
            }

            for (var i = 0; i < embeddings.Length; i++)
            {
                if (embeddings[i].Length != Metadata.Dimension)
                {
                    throw new InvalidOperationException(
                        $"Embedding API returned dimension {embeddings[i].Length} at index {i}, but {EmbeddingConfiguration.SectionName}:Dimension is {Metadata.Dimension}.");
                }
            }

            return embeddings;
        }
    }
}
