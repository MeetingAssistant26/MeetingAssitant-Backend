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

            return services;
        }
    }
}
