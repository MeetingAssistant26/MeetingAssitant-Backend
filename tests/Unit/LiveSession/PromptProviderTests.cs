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
    public void GetPersonalizedSummarizerPrompt_UsesParticipantPromptContextAndShaVersion()
    {
        var provider = new PromptProvider(new TestHostEnvironment(AppContext.BaseDirectory));
        const string transcript = "[00:00:01 Alice] We approved the backend deployment.";
        var context = $"Personalization context for Alice:{Environment.NewLine}- Job role: Backend Lead";

        var prompt = provider.GetPersonalizedSummarizerPrompt("Alice", transcript, context);

        prompt.Should().Contain("personalized meeting summaries");
        prompt.Should().Contain("Summary for Alice:");
        prompt.Should().Contain(context);
        prompt.Should().Contain($"Transcript:{Environment.NewLine}{transcript}");
        prompt.Should().NotContain("{participant}");
        prompt.Should().NotContain("{personalization_context}");
        prompt.Should().NotContain("{transcript}");
        provider.PersonalizedSummarizerPromptName.Should().Be("PersonalizedMeetingSummarizer");
        provider.PersonalizedSummarizerPromptVersion.Should().MatchRegex("^sha256:[0-9a-f]{64}$");
    }

    [Fact]
    public void GetPersonalizedSummarizerPrompt_OmitsContextSectionWhenNoPersonalizationValuesExist()
    {
        var provider = new PromptProvider(new TestHostEnvironment(AppContext.BaseDirectory));
        const string transcript = "[00:00:01 Alice] We approved the backend deployment.";

        var prompt = provider.GetPersonalizedSummarizerPrompt("Alice", transcript);

        prompt.Should().NotContain("Personalization context for Alice:");
        prompt.Should().Contain($"Transcript:{Environment.NewLine}{transcript}");
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

        var result = await service.SummarizeAsync(transcript);

        result.PromptName.Should().Be("MeetingSummarizer");
        result.PromptVersion.Should().MatchRegex("^sha256:[0-9a-f]{64}$");
        handler.RequestBody.Should().NotBeNullOrWhiteSpace();
        using var document = JsonDocument.Parse(handler.RequestBody!);
        var messages = document.RootElement.GetProperty("messages");
        messages.GetArrayLength().Should().Be(1);
        var content = messages[0].GetProperty("content").GetString();
        content.Should().Contain("You are an AI meeting assistant specialized in summarizing meetings.");
        content.Should().Contain($"Transcript:{Environment.NewLine}{transcript}");
        content.Should().NotContain("{transcript}");
    }

    [Fact]
    public async Task SummarizerService_SendsPersonalizedPromptWithTemperatureAndVersion()
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
        const string transcript = "[00:00:02 Alice] The dashboard redesign decision is final.";
        var context = $"Personalization context for Alice:{Environment.NewLine}- Context: owns frontend polish";

        var result = await service.SummarizePersonalizedAsync(transcript, "Alice", context);

        result.PromptName.Should().Be("PersonalizedMeetingSummarizer");
        result.PromptVersion.Should().MatchRegex("^sha256:[0-9a-f]{64}$");
        handler.RequestBody.Should().NotBeNullOrWhiteSpace();
        using var document = JsonDocument.Parse(handler.RequestBody!);
        document.RootElement.GetProperty("temperature").GetDouble().Should().Be(0.2);
        var content = document.RootElement.GetProperty("messages")[0].GetProperty("content").GetString();
        content.Should().Contain("Summary for Alice:");
        content.Should().Contain(context);
        content.Should().Contain($"Transcript:{Environment.NewLine}{transcript}");
        content.Should().NotContain("{participant}");
        content.Should().NotContain("{personalization_context}");
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
