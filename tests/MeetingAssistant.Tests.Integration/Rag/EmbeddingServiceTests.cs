using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using FluentAssertions;
using MeetingAssistant.Features.LiveSession.Infrastructure;
using MeetingAssistant.Infrastructure.AI;
using MeetingAssistant.Tests.Integration.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace MeetingAssistant.Tests.Integration.Rag;

public sealed class EmbeddingServiceTests : IClassFixture<MeetingAssistantWebFactory>
{
    private readonly MeetingAssistantWebFactory _factory;

    public EmbeddingServiceTests(MeetingAssistantWebFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Deterministic_provider_returns_stable_vectors_and_metadata()
    {
        var service = new DeterministicEmbeddingService(Options.Create(OptionsFor(
            EmbeddingProviderNames.DeterministicTest,
            model: "deterministic-test-v1",
            dimension: 8)));

        service.Metadata.Should().Be(new EmbeddingMetadata(
            EmbeddingProviderNames.DeterministicTest,
            "deterministic-test-v1",
            8));

        var first = await service.EmbedAsync("roadmap priorities", CancellationToken.None);
        var second = await service.EmbedAsync("roadmap priorities", CancellationToken.None);
        var different = await service.EmbedAsync("budget review", CancellationToken.None);

        first.Should().HaveCount(8);
        first.Should().Equal(second);
        first.Should().NotEqual(different);
    }

    [Fact]
    public async Task Service_provider_resolves_explicit_deterministic_provider_for_tests()
    {
        using var scope = _factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IEmbeddingService>();

        service.Metadata.Provider.Should().Be(EmbeddingProviderNames.DeterministicTest);
        service.Metadata.Model.Should().Be("deterministic-test-v1");
        service.Metadata.Dimension.Should().Be(1536);

        var embedding = await service.EmbedAsync("factory deterministic embedding", CancellationToken.None);
        embedding.Should().HaveCount(1536);
    }

    [Fact]
    public void Unsupported_provider_fails_clearly_instead_of_falling_back_to_chromadb_or_json()
    {
        var error = EmbeddingConfiguration.TryGetValidationError(ProviderConfig(
            provider: "chromadb",
            baseUrl: "http://chroma:8000",
            model: "legacy-chroma",
            dimension: 1536));

        error.Should().Contain("Unsupported OpenAiCompatible:Embedding:Provider 'chromadb'");
        error.Should().Contain(EmbeddingProviderNames.OpenAiCompatible);
        error.Should().Contain(EmbeddingProviderNames.DeterministicTest);
    }

    [Fact]
    public void Openai_compatible_provider_requires_base_url_model_and_dimension()
    {
        EmbeddingConfiguration.TryGetValidationError(ProviderConfig(
                provider: EmbeddingProviderNames.OpenAiCompatible,
                baseUrl: "",
                model: "text-embedding-3-small",
                dimension: 1536))
            .Should().Contain("BaseUrl must be configured");

        EmbeddingConfiguration.TryGetValidationError(ProviderConfig(
                provider: EmbeddingProviderNames.OpenAiCompatible,
                baseUrl: "not-a-url",
                model: "text-embedding-3-small",
                dimension: 1536))
            .Should().Contain("absolute HTTP(S) URI");

        EmbeddingConfiguration.TryGetValidationError(ProviderConfig(
                provider: EmbeddingProviderNames.OpenAiCompatible,
                baseUrl: "http://embeddings.test/v1",
                model: "",
                dimension: 1536))
            .Should().Contain("Model must be configured");

        EmbeddingConfiguration.TryGetValidationError(ProviderConfig(
                provider: EmbeddingProviderNames.OpenAiCompatible,
                baseUrl: "http://embeddings.test/v1",
                model: "text-embedding-3-small",
                dimension: 0))
            .Should().Contain("Dimension must be greater than zero");
    }

    [Fact]
    public async Task Openai_compatible_provider_posts_to_configured_endpoint_and_preserves_metadata()
    {
        HttpRequestMessage? capturedRequest = null;
        string? capturedBody = null;
        var handler = new CaptureHandler(async request =>
        {
            capturedRequest = request;
            capturedBody = await request.Content!.ReadAsStringAsync();

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    {
                      "object": "list",
                      "model": "prod-embedding-v1",
                      "data": [
                        { "object": "embedding", "index": 1, "embedding": [0.3, 0.4, 0.5] },
                        { "object": "embedding", "index": 0, "embedding": [0.0, 0.1, 0.2] }
                      ],
                      "usage": { "prompt_tokens": 4, "total_tokens": 4 }
                    }
                    """)
            };
        });

        using var httpClient = new HttpClient(handler);
        httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-api-key");

        var service = new OpenAiCompatibleEmbeddingService(
            httpClient,
            Options.Create(OptionsFor(
                EmbeddingProviderNames.OpenAiCompatible,
                baseUrl: "http://embeddings.test/v1",
                model: "prod-embedding-v1",
                dimension: 3)));

        var embeddings = await service.EmbedBatchAsync(["first", "second"], CancellationToken.None);

        service.Metadata.Should().Be(new EmbeddingMetadata(
            EmbeddingProviderNames.OpenAiCompatible,
            "prod-embedding-v1",
            3));
        embeddings.Should().BeEquivalentTo(new[]
        {
            new[] { 0.0f, 0.1f, 0.2f },
            new[] { 0.3f, 0.4f, 0.5f }
        }, options => options.WithStrictOrdering());

        capturedRequest.Should().NotBeNull();
        capturedRequest!.RequestUri.Should().Be("http://embeddings.test/v1/embeddings");
        capturedRequest.Headers.Authorization?.Scheme.Should().Be("Bearer");
        capturedRequest.Headers.Authorization?.Parameter.Should().Be("test-api-key");

        using var bodyJson = JsonDocument.Parse(capturedBody!);
        bodyJson.RootElement.GetProperty("model").GetString().Should().Be("prod-embedding-v1");
        bodyJson.RootElement.GetProperty("dimensions").GetInt32().Should().Be(3);
        bodyJson.RootElement.GetProperty("input").EnumerateArray().Select(x => x.GetString())
            .Should().Equal("first", "second");
    }

    [Fact]
    public async Task Openai_compatible_provider_rejects_response_dimension_mismatch()
    {
        var handler = new CaptureHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""
                {
                  "data": [
                    { "index": 0, "embedding": [0.0, 0.1, 0.2] }
                  ]
                }
                """)
        }));

        using var httpClient = new HttpClient(handler);
        var service = new OpenAiCompatibleEmbeddingService(
            httpClient,
            Options.Create(OptionsFor(
                EmbeddingProviderNames.OpenAiCompatible,
                baseUrl: "http://embeddings.test/v1",
                model: "prod-embedding-v1",
                dimension: 2)));

        var act = () => service.EmbedAsync("dimension mismatch", CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*returned dimension 3*OpenAiCompatible:Embedding:Dimension is 2*");
    }

    private static OpenAiCompatibleOptions OptionsFor(
        string provider,
        string baseUrl = "",
        string model = "test-model",
        int dimension = 3)
        => new()
        {
            Embedding = ProviderConfig(provider, baseUrl, model, dimension)
        };

    private static OpenAiCompatibleOptions.ProviderConfig ProviderConfig(
        string provider,
        string baseUrl,
        string model,
        int? dimension)
        => new()
        {
            Provider = provider,
            BaseUrl = baseUrl,
            ApiKey = "test-key",
            Model = model,
            Dimension = dimension
        };

    private sealed class CaptureHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handle)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => handle(request);
    }
}
