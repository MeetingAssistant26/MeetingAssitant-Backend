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

        const string meetingContext = "- Meeting reference date (UTC): 2026-06-01";
        const string peopleContext = "- user_id=11111111-1111-1111-1111-111111111111; display_name=Alice";

        var prompt = provider.GetTaskExtractionPrompt(transcript, meetingContext, peopleContext);

        prompt.Should().Contain("Your ONLY job is to return a valid JSON array of tasks.");
        prompt.Should().Contain("\"assigned_user_id\"");
        prompt.Should().Contain("\"deadline_date\"");
        prompt.Should().Contain(meetingContext);
        prompt.Should().Contain(peopleContext);
        prompt.Should().Contain($"Transcript:{Environment.NewLine}{transcript}");
        prompt.Should().NotContain("{transcript}");
        prompt.Should().NotContain("{meeting_context}");
        prompt.Should().NotContain("{people_context}");
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
        prompt.Should().Contain("What you said:");
        prompt.Should().NotContain("What Alice said:");
        prompt.Should().Contain("Role/context relevance:");
        prompt.Should().Contain(
            "include exactly one concise Role/context relevance sentence");
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
        content.Should().Contain("What you said:");
        content.Should().NotContain("What Alice said:");
        content.Should().Contain("Role/context relevance:");
        content.Should().Contain(
            "include exactly one concise Role/context relevance sentence");
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
