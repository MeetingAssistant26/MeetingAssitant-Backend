using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using MeetingAssistant.Features.LiveSession.Infrastructure;
using MeetingAssistant.Features.LiveSession.Services;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Xunit;

namespace tests.Unit.LiveSession;

public sealed class PromptProviderTests
{
    [Fact]
    public void GetSummarizerPrompt_UsesVendoredAiWorkPromptAndFillsTranscriptExactly()
    {
        var provider = new PromptProvider(new TestHostEnvironment(AppContext.BaseDirectory));
        const string transcript = "[00:00:01 Alice] We improved the NLP pipeline.";

        var prompt = provider.GetSummarizerPrompt(transcript);

        prompt.Should().Contain("You are an AI meeting assistant specialized in summarizing meetings.");
        prompt.Should().Contain($"Transcript:{Environment.NewLine}{transcript}");
        prompt.Should().NotContain("{transcript}");
    }

    [Fact]
    public void GetTaskExtractionPrompt_UsesVendoredAiWorkSchemaAndFillsTranscriptExactly()
    {
        var provider = new PromptProvider(new TestHostEnvironment(AppContext.BaseDirectory));
        const string transcript = "SPEAKER_01: I will review the dataset today.";

        var prompt = provider.GetTaskExtractionPrompt(transcript);

        prompt.Should().Contain("\"tasks\": [");
        prompt.Should().Contain("\"due_date\": \"exact deadline as mentioned in text, or null\"");
        prompt.Should().Contain($"Transcript:{Environment.NewLine}{transcript}");
        prompt.Should().NotContain("{transcript}");
    }

    [Fact]
    public async Task SummarizerService_SendsFilledAiWorkPromptAsSingleLlmMessage()
    {
        var handler = new CapturingHandler();
        var service = new SummarizerService(
            Options.Create(new OpenAiCompatibleOptions
            {
                Llm = new OpenAiCompatibleOptions.ProviderConfig
                {
                    BaseUrl = "http://llm.test/v1",
                    ApiKey = "test-key",
                    Model = "local"
                }
            }),
            new TestHttpClientFactory(handler),
            new PromptProvider(new TestHostEnvironment(AppContext.BaseDirectory)));
        const string transcript = "[00:00:02 Bob] The dashboard redesign decision is final.";

        await service.SummarizeAsync(transcript);

        handler.RequestBody.Should().NotBeNullOrWhiteSpace();
        using var document = JsonDocument.Parse(handler.RequestBody!);
        var messages = document.RootElement.GetProperty("messages");
        messages.GetArrayLength().Should().Be(1);
        var content = messages[0].GetProperty("content").GetString();
        content.Should().Contain("You are an AI meeting assistant specialized in summarizing meetings.");
        content.Should().Contain($"Transcript:{Environment.NewLine}{transcript}");
        content.Should().NotContain("{transcript}");
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public string? RequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"choices\":[{\"message\":{\"content\":\"summary\"}}]}",
                    Encoding.UTF8,
                    "application/json")
            };
        }
    }

    private sealed class TestHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class TestHostEnvironment(string contentRootPath) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "MeetingAssistant.Tests";
        public string ContentRootPath { get; set; } = contentRootPath;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
