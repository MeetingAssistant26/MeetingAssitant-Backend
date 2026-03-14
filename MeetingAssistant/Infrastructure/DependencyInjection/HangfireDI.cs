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
                
                GlobalJobFilters.Filters.Add(new Api.Infrastructure.Hangfire.HangfireCorrelationFilter());
                GlobalJobFilters.Filters.Add(new Api.Infrastructure.Hangfire.HangfireRetryFilter());
            });


            // Add the processing server as IHostedService
            services.AddHangfireServer();

            return services;
        }

        public static void UseSecureHangfireDashboard(this Microsoft.AspNetCore.Builder.WebApplication app)
        {
            app.UseHangfireDashboard("/jobs", new DashboardOptions
            {
                Authorization =
                [
                    new HangfireBasicAuthenticationFilter.HangfireCustomBasicAuthenticationFilter
                    {
                        User = app.Configuration["HangfireSettings:DashboardUsername"],
                        Pass = app.Configuration["HangfireSettings:DashboardPassword"]
                    }
                ]
            });
        }
    }
}
