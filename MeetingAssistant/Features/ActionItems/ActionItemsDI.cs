using MeetingAssistant.Features.ActionItems.Services;
using MeetingAssistant.Features.ActionItems.Services.Abstractions;
using MeetingAssistant.Features.ActionItems.Services.Providers.Trello;

namespace MeetingAssistant.Features.ActionItems
{
    public static class ActionItemsDI
    {
        public static IServiceCollection AddActionItemsFeature(this IServiceCollection services)
        {
            services.AddMemoryCache();
            services.AddHttpClient<TrelloTaskProvider>();
            services.AddScoped<ITaskProviderFactory, TaskProviderFactory>();
            services.AddScoped<ITaskProvider, TrelloTaskProvider>();

            services.AddScoped<IActionItemService, ActionItemService>();
            services.AddScoped<IIntegrationAdminService, IntegrationAdminService>();
            services.AddScoped<IUserIntegrationService, UserIntegrationService>();

            return services;
        }
    }
}
