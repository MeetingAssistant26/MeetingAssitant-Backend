using FluentAssertions;
using MeetingAssistant.Features.LiveSession;
using MeetingAssistant.Features.LiveSession.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace MeetingAssistant.Tests.Unit.LiveSession;

public class OpenAiCompatibleOptionsTests
{
    [Fact]
    public void AddLiveSessionFeature_BindsDockerStyleOpenAiCompatibleSettings()
    {
        using var provider = BuildProvider(new Dictionary<string, string?>
        {
            ["OpenAiCompatible:Stt:BaseUrl"] = "http://stt:8000/v1",
            ["OpenAiCompatible:Stt:ApiKey"] = "local-ai-key",
            ["OpenAiCompatible:Stt:Model"] = "whisper-1",
            ["OpenAiCompatible:Llm:BaseUrl"] = "http://llm:8000/v1",
            ["OpenAiCompatible:Llm:ApiKey"] = "local-ai-key",
            ["OpenAiCompatible:Llm:Model"] = "local"
        });

        var options = provider.GetRequiredService<IOptions<OpenAiCompatibleOptions>>().Value;

        options.Stt.BaseUrl.Should().Be("http://stt:8000/v1");
        options.Stt.Model.Should().Be("whisper-1");
        options.Llm.BaseUrl.Should().Be("http://llm:8000/v1");
        options.Llm.Model.Should().Be("local");
    }

    [Fact]
    public void AddLiveSessionFeature_RejectsMissingLlmBaseUrl()
    {
        using var provider = BuildProvider(new Dictionary<string, string?>
        {
            ["OpenAiCompatible:Stt:BaseUrl"] = "http://stt:8000/v1",
            ["OpenAiCompatible:Stt:Model"] = "whisper-1",
            ["OpenAiCompatible:Llm:Model"] = "local"
        });

        var act = () => provider.GetRequiredService<IOptions<OpenAiCompatibleOptions>>().Value;

        act.Should().Throw<OptionsValidationException>()
            .WithMessage("*OpenAiCompatible:Stt and OpenAiCompatible:Llm*");
    }

    private static ServiceProvider BuildProvider(IReadOnlyDictionary<string, string?> settings)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings)
            .Build();

        var services = new ServiceCollection();
        services.AddLiveSessionFeature(configuration);

        return services.BuildServiceProvider(validateScopes: true);
    }
}
