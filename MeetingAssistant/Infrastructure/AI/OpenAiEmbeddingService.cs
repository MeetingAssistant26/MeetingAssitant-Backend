using System.Net.Http.Json;
using MeetingAssistant.Api.Infrastructure.Configuration;
using MeetingAssistant.Infrastructure.AI.DTOs;
using Microsoft.Extensions.Options;

namespace MeetingAssistant.Infrastructure.AI
{
    public sealed class OpenAiEmbeddingService(
        HttpClient httpClient,
        IOptions<AiSettings> aiSettings) : IEmbeddingService
    {
        private const string Model = "text-embedding-3-small";
        private readonly HttpClient _httpClient = httpClient;
        private readonly AiSettings _aiSettings = aiSettings.Value;

        public async Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken)
        {
            var request = new EmbeddingRequest
            {
                Model = Model,
                Input = text
            };

            var response = await _httpClient.PostAsJsonAsync(
                $"{_aiSettings.BaseUrl}/v1/embeddings",
                request,
                cancellationToken);

            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<EmbeddingResponse>(cancellationToken)
                ?? throw new InvalidOperationException("Embedding API returned null response.");

            return result.Data?.FirstOrDefault()?.Embedding
                ?? throw new InvalidOperationException("Embedding API returned no embedding data.");
        }

        public async Task<float[][]> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken)
        {
            var request = new EmbeddingRequest
            {
                Model = Model,
                Input = texts
            };

            var response = await _httpClient.PostAsJsonAsync(
                $"{_aiSettings.BaseUrl}/v1/embeddings",
                request,
                cancellationToken);

            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<EmbeddingResponse>(cancellationToken)
                ?? throw new InvalidOperationException("Embedding API returned null response.");

            return result.Data?
                .OrderBy(d => d.Index)
                .Select(d => d.Embedding ?? throw new InvalidOperationException($"Embedding at index {d.Index} was null."))
                .ToArray()
                ?? throw new InvalidOperationException("Embedding API returned no embedding data.");
        }
    }
}
