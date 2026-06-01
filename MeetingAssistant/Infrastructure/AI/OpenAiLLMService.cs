using System.Net.Http.Json;
using System.Net.Http.Headers;
using System.Text.Json;
using MeetingAssistant.Features.LiveSession.Infrastructure;
using MeetingAssistant.Infrastructure.AI.DTOs;
using Microsoft.Extensions.Options;

namespace MeetingAssistant.Infrastructure.AI
{
    public sealed class OpenAiLLMService(
        HttpClient httpClient,
        IOptions<OpenAiCompatibleOptions> openAiCompatibleOptions) : ILLMService
    {
        private readonly HttpClient _httpClient = httpClient;
        private readonly OpenAiCompatibleOptions.ProviderConfig _llm = openAiCompatibleOptions.Value.Llm;

        public async Task<LLMResponse> CompleteAsync(LLMRequest request, CancellationToken cancellationToken)
        {
            if (!string.IsNullOrWhiteSpace(_llm.ApiKey)
                && _httpClient.DefaultRequestHeaders.Authorization == null)
            {
                _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _llm.ApiKey);
            }

            var response = await _httpClient.PostAsJsonAsync(
                $"{NormalizeBaseUrl(_llm.BaseUrl)}/chat/completions",
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

        private static string NormalizeBaseUrl(string baseUrl)
        {
            return (baseUrl ?? string.Empty).TrimEnd('/');
        }
    }
}
