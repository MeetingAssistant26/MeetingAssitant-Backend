using System;
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
                    }, new PostgreSqlStorageOptions
                    {
                        SchemaName = "hangfire",
                        PrepareSchemaIfNecessary = true,
                        QueuePollInterval = TimeSpan.FromSeconds(5)
                    });

                // Disable built-in AutomaticRetryAttribute (Attempts = 0)
                // We'll replace this with a custom retry filter in later states.
                GlobalJobFilters.Filters.Add(new AutomaticRetryAttribute { Attempts = 0 });
            });


            // Add the processing server as IHostedService
            services.AddHangfireServer();

            return services;
        }
    }
}
