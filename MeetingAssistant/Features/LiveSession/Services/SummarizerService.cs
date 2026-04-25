using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using MeetingAssistant.Features.LiveSession.Infrastructure;
using Microsoft.Extensions.Options;

namespace MeetingAssistant.Features.LiveSession.Services
{
    public class SummarizerService(
        IOptions<OpenAiCompatibleOptions> options,
        IHttpClientFactory httpClientFactory,
        IPromptProvider promptProvider) : ISummarizerService
    {
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

        private readonly OpenAiCompatibleOptions.ProviderConfig _llm = options.Value.Llm;
        private readonly IHttpClientFactory _httpClientFactory = httpClientFactory;
        private readonly IPromptProvider _promptProvider = promptProvider;

        public async Task<SummaryResult> SummarizeAsync(string fullTranscript, CancellationToken ct = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(fullTranscript);

            var baseUrl = NormalizeBaseUrl(_llm.BaseUrl);
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/chat/completions")
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(new
                    {
                        model = _llm.Model,
                        messages = new object[]
                        {
                            new { role = "system", content = _promptProvider.GetSummarizerPrompt() },
                            new { role = "user", content = fullTranscript }
                        }
                    }, JsonOptions),
                    Encoding.UTF8,
                    "application/json")
            };

            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _llm.ApiKey);

            using var client = _httpClientFactory.CreateClient("openai-llm");
            using var response = await client.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();

            var body = await response.Content.ReadAsStringAsync(ct);
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;

            var summaryText = string.Empty;
            if (root.TryGetProperty("choices", out var choices)
                && choices.ValueKind == JsonValueKind.Array
                && choices.GetArrayLength() > 0
                && choices[0].TryGetProperty("message", out var message)
                && message.ValueKind == JsonValueKind.Object
                && message.TryGetProperty("content", out var content)
                && content.ValueKind == JsonValueKind.String)
            {
                summaryText = content.GetString() ?? string.Empty;
            }

            int? promptTokens = null;
            int? completionTokens = null;
            if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
            {
                if (usage.TryGetProperty("prompt_tokens", out var prompt) && prompt.TryGetInt32(out var promptValue))
                {
                    promptTokens = promptValue;
                }

                if (usage.TryGetProperty("completion_tokens", out var completion)
                    && completion.TryGetInt32(out var completionValue))
                {
                    completionTokens = completionValue;
                }
            }

            return new SummaryResult(summaryText, _llm.Model, promptTokens, completionTokens);
        }

        private static string NormalizeBaseUrl(string baseUrl)
        {
            return (baseUrl ?? string.Empty).TrimEnd('/');
        }
    }
}
