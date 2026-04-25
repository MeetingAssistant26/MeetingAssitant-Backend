using MeetingAssistant.Features.LiveSession.Hubs;
using MeetingAssistant.Features.LiveSession.Jobs;
using MeetingAssistant.Features.LiveSession.Services;
using Microsoft.Extensions.DependencyInjection;

namespace MeetingAssistant.Features.LiveSession
{
    public static class LiveSessionDI
    {
        public static IServiceCollection AddLiveSessionFeature(this IServiceCollection services)
        {
            services.AddScoped<ISessionService, SessionService>();
            services.AddScoped<ILiveKitTokenIssuer, LiveKitTokenIssuer>();
            services.AddScoped<ILiveKitWebhookValidator, LiveKitWebhookValidator>();
            services.AddScoped<IWebhookService, WebhookService>();
            services.AddScoped<IStorageService, StorageService>();
            services.AddScoped<ILiveSessionNotifier, LiveSessionNotifier>();
            services.AddScoped<IngestParticipantAudioJob>();

            return services;
        }
    }
}
