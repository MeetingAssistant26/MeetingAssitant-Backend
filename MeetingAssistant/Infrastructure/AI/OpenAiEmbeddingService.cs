using System.Net.Http.Json;
using System.Security.Cryptography;
using MeetingAssistant.Api.Infrastructure.Configuration;
using MeetingAssistant.Features.LiveSession.Infrastructure;
using MeetingAssistant.Infrastructure.AI.DTOs;
using Microsoft.Extensions.Options;

namespace MeetingAssistant.Infrastructure.AI
{
    public sealed class OpenAiEmbeddingService(
        HttpClient httpClient,
        IOptions<AiSettings> aiSettings,
        IOptions<OpenAiCompatibleOptions> openAiCompatibleOptions) : IEmbeddingService
    {
        private const string DefaultModel = "text-embedding-3-small";
        private const int DefaultDimension = 1536;
        private const string DeterministicTestProvider = "deterministic-test";
        private readonly HttpClient _httpClient = httpClient;
        private readonly AiSettings _aiSettings = aiSettings.Value;
        private readonly OpenAiCompatibleOptions.ProviderConfig _embeddingOptions = openAiCompatibleOptions.Value.Embedding;

        public async Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken)
        {
            if (IsDeterministicTestProvider())
            {
                return CreateDeterministicEmbedding(text, ResolveDimension());
            }

            var request = new EmbeddingRequest
            {
                Model = ResolveModel(),
                Input = text
            };

            var response = await _httpClient.PostAsJsonAsync(
                ResolveEmbeddingEndpoint(),
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
            if (IsDeterministicTestProvider())
            {
                var dimension = ResolveDimension();
                return texts.Select(text => CreateDeterministicEmbedding(text, dimension)).ToArray();
            }

            var request = new EmbeddingRequest
            {
                Model = ResolveModel(),
                Input = texts
            };

            var response = await _httpClient.PostAsJsonAsync(
                ResolveEmbeddingEndpoint(),
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

        private bool IsDeterministicTestProvider() =>
            string.Equals(_embeddingOptions.Provider, DeterministicTestProvider, StringComparison.OrdinalIgnoreCase);

        private string ResolveModel() =>
            string.IsNullOrWhiteSpace(_embeddingOptions.Model) ? DefaultModel : _embeddingOptions.Model;

        private int ResolveDimension() => _embeddingOptions.Dimension.GetValueOrDefault(DefaultDimension);

        private string ResolveEmbeddingEndpoint()
        {
            if (!string.IsNullOrWhiteSpace(_embeddingOptions.BaseUrl))
            {
                return $"{_embeddingOptions.BaseUrl.TrimEnd('/')}/embeddings";
            }

            var baseUrl = _aiSettings.BaseUrl;

            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                throw new InvalidOperationException("Embedding API base URL is not configured.");
            }

            return $"{baseUrl.TrimEnd('/')}/v1/embeddings";
        }

        private static float[] CreateDeterministicEmbedding(string text, int dimension)
        {
            if (dimension <= 0)
            {
                throw new InvalidOperationException("Embedding dimension must be greater than zero.");
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
