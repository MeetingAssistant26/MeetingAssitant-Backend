using Microsoft.Extensions.DependencyInjection;
using MeetingAssistant.Features.Tasks.Services;

namespace MeetingAssistant.Features.Tasks
{
    public static class TasksDI
    {
        public static IServiceCollection AddTasksFeature(this IServiceCollection services)
        {
            services.AddScoped<IReminderService, ReminderService>();

            return services;
        }
    }
}
