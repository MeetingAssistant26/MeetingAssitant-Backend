using MeetingAssistant.Features.AgentApi.Services;

namespace MeetingAssistant.Features.AgentApi
{
    public static class AgentApiDI
    {
        public static IServiceCollection AddAgentApiFeature(this IServiceCollection services)
        {
            services.AddScoped<IAgentAuthService, AgentAuthService>();
            services.AddScoped<IAgentContextService, AgentContextService>();
            services.AddScoped<IAgentReminderService, AgentReminderService>();

            return services;
        }
    }
}
