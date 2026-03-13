using System.Net.Http.Json;
using System.Text.Json;
using MeetingAssistant.Api.Infrastructure.Configuration;
using MeetingAssistant.Infrastructure.AI.DTOs;
using Microsoft.Extensions.Options;

namespace MeetingAssistant.Infrastructure.AI
{
    public sealed class OpenAiLLMService(
        HttpClient httpClient,
        IOptions<AiSettings> aiSettings) : ILLMService
    {
        private readonly HttpClient _httpClient = httpClient;
        private readonly AiSettings _aiSettings = aiSettings.Value;

        public async Task<LLMResponse> CompleteAsync(LLMRequest request, CancellationToken cancellationToken)
        {
            var response = await _httpClient.PostAsJsonAsync(
                $"{_aiSettings.BaseUrl}/v1/chat/completions",
                request,
                cancellationToken);

            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<LLMResponse>(cancellationToken)
                ?? throw new InvalidOperationException("LLM API returned null response.");

            return result;
        }

        public async Task<T> CompleteWithJsonAsync<T>(LLMRequest request, CancellationToken cancellationToken)
        {
            var jsonRequest = request with
            {
                ResponseFormat = new ResponseFormat { Type = "json_object" }
            };

            var llmResponse = await CompleteAsync(jsonRequest, cancellationToken);

            var content = llmResponse.Choices?.FirstOrDefault()?.Message?.Content
                ?? throw new InvalidOperationException("LLM API returned no content.");

            return JsonSerializer.Deserialize<T>(content)
                ?? throw new InvalidOperationException($"Failed to deserialize LLM response to {typeof(T).Name}.");
        }
    }
}
