using MeetingAssistant.Features.LiveSession.Hubs;
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
            services.Configure<OpenAiCompatibleOptions>(configuration.GetSection("OpenAiCompatible"));
            services.AddScoped<ISessionService, SessionService>();
            services.AddScoped<ILiveKitTokenIssuer, LiveKitTokenIssuer>();
            services.AddScoped<ILiveKitWebhookValidator, LiveKitWebhookValidator>();
            services.AddScoped<IWebhookService, WebhookService>();
            services.AddScoped<IStorageService, StorageService>();
            services.AddScoped<IEgressService, EgressService>();
            services.AddSingleton<IPromptProvider, PromptProvider>();
            services.AddScoped<ISttService, SttService>();
            services.AddScoped<ISummarizerService, SummarizerService>();
            services.AddScoped<ILiveSessionNotifier, LiveSessionNotifier>();
            services.AddScoped<IngestParticipantAudioJob>();
            services.AddScoped<GenerateMeetingTranscriptJob>();
            services.AddScoped<GenerateMeetingSummaryJob>();
            services.AddHttpClient("openai-stt");
            services.AddHttpClient("openai-llm");

            return services;
        }
    }
}
