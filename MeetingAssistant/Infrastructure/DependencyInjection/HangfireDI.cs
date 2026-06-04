using System;
using Hangfire;
using Hangfire.PostgreSql;
using MeetingAssistant.Api.Infrastructure.Hangfire;
using MeetingAssistant.Features.Identity.Services;
using MeetingAssistant.Features.LiveSession.Jobs;

namespace MeetingAssistant.Infrastructure.DependencyInjection
{
    public static class HangfireDI
    {
        public static IServiceCollection AddHangfireServices(this IServiceCollection services, IConfiguration configuration)
        {
            services.AddSingleton<IHangfireJobContextAccessor, HangfireJobContextAccessor>();
            services.AddSingleton<HangfireJobContextFilter>();
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

        public static void RegisterRecurringJobs(this Microsoft.AspNetCore.Builder.WebApplication app)
        {
            RecurringJob.AddOrUpdate<RefreshTokenCleanupService>(
                "cleanup-expired-refresh-tokens",
                service => service.CleanupExpiredTokensAsync(default),
                Cron.Daily);

            RecurringJob.AddOrUpdate<PostMeetingProcessingReconciliationJob>(
                "reconcile-stale-post-meeting-processing",
                job => job.RunAsync(default),
                Cron.Hourly);

            RecurringJob.AddOrUpdate<ParticipantAudioEgressReconciliationJob>(
                "reconcile-participant-audio-egress",
                job => job.RunAsync(default),
                Cron.Minutely);
        }

        public static void ConfigureHangfireJobContextFilter(this Microsoft.AspNetCore.Builder.WebApplication app)
        {
            GlobalJobFilters.Filters.Add(app.Services.GetRequiredService<HangfireJobContextFilter>());
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
