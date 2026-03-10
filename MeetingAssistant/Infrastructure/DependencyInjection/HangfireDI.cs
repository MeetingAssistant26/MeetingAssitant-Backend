using Hangfire;
using Hangfire.PostgreSql;

namespace MeetingAssistant.Infrastructure.DependencyInjection
{
    public static class HangfireDI
    {
        public static IServiceCollection AddHangfireServices(this IServiceCollection services, IConfiguration configuration)
        {
            // Add Hangfire services.
            services.AddHangfire(config =>
            {
                config
                    .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
                    .UseSimpleAssemblyNameTypeSerializer()
                    .UseRecommendedSerializerSettings()
                    .UsePostgreSqlStorage(options =>
                    {
                        options.UseNpgsqlConnection(
                            configuration.GetConnectionString("DefaultConnection"));
                    });
            });


            // Add the processing server as IHostedService
            services.AddHangfireServer();

            return services;
        }
    }
}
