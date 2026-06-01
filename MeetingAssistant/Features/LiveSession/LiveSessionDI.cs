using MeetingAssistant.Features.LiveSession.Infrastructure;
using MeetingAssistant.Features.LiveSession.Jobs;
using MeetingAssistant.Features.LiveSession.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MeetingAssistant.Features.LiveSession
{
    public static class LiveSessionDI
    {
        public static IServiceCollection AddLiveSessionFeature(
            this IServiceCollection services,
            IConfiguration configuration)
        {
            services.AddOptions<OpenAiCompatibleOptions>()
                .Bind(configuration.GetSection("OpenAiCompatible"))
                .Validate(
                    options => IsValidProvider(options.Stt) && IsValidProvider(options.Llm),
                    "OpenAiCompatible:Stt and OpenAiCompatible:Llm must each specify an absolute HTTP(S) BaseUrl and non-empty Model.")
                .ValidateOnStart();
            services.Configure<AiDebugOptions>(configuration.GetSection("AiDebug"));
            services.AddScoped<ISessionService, SessionService>();
            services.AddScoped<IMeetingArtifactService, MeetingArtifactService>();
            services.AddScoped<ILiveKitTokenIssuer, LiveKitTokenIssuer>();
            services.AddScoped<IAiAssistantDispatchService, AiAssistantDispatchService>();
            services.AddScoped<IAiDebugTraceService, AiDebugTraceService>();
            services.AddScoped<ILiveKitWebhookValidator, LiveKitWebhookValidator>();
            services.AddScoped<IWebhookService, WebhookService>();
            services.AddScoped<IStorageService, StorageService>();
            services.AddScoped<IEgressService, EgressService>();
            services.AddSingleton<IPromptProvider, PromptProvider>();
            services.AddScoped<ISttService, SttService>();
            services.AddScoped<ISummarizerService, SummarizerService>();
            services.AddScoped<IngestParticipantAudioJob>();
            services.AddScoped<GenerateMeetingTranscriptJob>();
            services.AddScoped<GenerateMeetingSummaryJob>();
            services.AddHttpClient("openai-stt");
            services.AddHttpClient("openai-llm");
            services.AddHttpClient("livekit-agent-dispatch");

            return services;
        }

        private static bool IsValidProvider(OpenAiCompatibleOptions.ProviderConfig provider)
        {
            if (string.IsNullOrWhiteSpace(provider.Model)
                || string.IsNullOrWhiteSpace(provider.BaseUrl)
                || !Uri.TryCreate(provider.BaseUrl, UriKind.Absolute, out var uri))
            {
                return false;
            }

            return uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps;
        }
    }
}
