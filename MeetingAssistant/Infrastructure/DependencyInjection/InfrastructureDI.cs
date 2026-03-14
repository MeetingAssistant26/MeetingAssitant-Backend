using MeetingAssistant.Api.Infrastructure.Configuration;
using MeetingAssistant.Api.Infrastructure.Services;
using MeetingAssistant.Infrastructure.AI;
using MeetingAssistant.Shared.Errors;
using MeetingAssistant.Shared.Settings;
using Microsoft.Extensions.Http;
using Polly;
using Polly.Extensions.Http;
using System.Reflection;

namespace MeetingAssistant.Infrastructure.DependencyInjection
{
    public static class InfrastructureDI
    {
        public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
        {
            services.AddControllers();

            services.AddCors(options =>
                options.AddDefaultPolicy(builder =>
                builder.AllowAnyOrigin()
                .AllowAnyMethod()
                .AllowAnyHeader()
                )
            );

            services.AddProblemDetails();

            services.AddHttpContextAccessor();
            services.Configure<MailSettings>(configuration.GetSection(nameof(MailSettings)));

            // Configuration bindings
            services.Configure<RedisSettings>(configuration.GetSection("Redis"));
            services.Configure<AiSettings>(configuration.GetSection("AI"));
            services.Configure<LiveKitSettings>(configuration.GetSection("LiveKit"));

            // Scoped infrastructure services
            services.AddScoped<ITenantProvider, TenantProvider>();
            services.AddScoped<ICorrelationIdProvider, CorrelationIdProvider>();

            // MediatR assembly scan
            services.AddMediatR(cfg => cfg.RegisterServicesFromAssembly(Assembly.GetExecutingAssembly()));

            // AI services with typed HttpClient + Polly policies
            services.AddHttpClient<ILLMService, OpenAiLLMService>(client =>
            {
                var aiSettings = configuration.GetSection("AI").Get<AiSettings>();
                if (!string.IsNullOrEmpty(aiSettings?.ApiKey))
                    client.DefaultRequestHeaders.Authorization =
                        new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", aiSettings.ApiKey);
            })
            .AddPolicyHandler(GetRetryPolicy())
            .AddPolicyHandler(GetCircuitBreakerPolicy());

            services.AddHttpClient<IEmbeddingService, OpenAiEmbeddingService>(client =>
            {
                var aiSettings = configuration.GetSection("AI").Get<AiSettings>();
                if (!string.IsNullOrEmpty(aiSettings?.ApiKey))
                    client.DefaultRequestHeaders.Authorization =
                        new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", aiSettings.ApiKey);
            })
            .AddPolicyHandler(GetRetryPolicy())
            .AddPolicyHandler(GetCircuitBreakerPolicy());

            return services;
        }

        private static IAsyncPolicy<HttpResponseMessage> GetRetryPolicy()
        {
            return HttpPolicyExtensions
                .HandleTransientHttpError()
                .WaitAndRetryAsync(3, retryAttempt =>
                    TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)));
        }

        private static IAsyncPolicy<HttpResponseMessage> GetCircuitBreakerPolicy()
        {
            return HttpPolicyExtensions
                .HandleTransientHttpError()
                .CircuitBreakerAsync(5, TimeSpan.FromSeconds(30));
        }

        public static IServiceCollection AddCachingAndHealthChecks(this IServiceCollection services, IConfiguration configuration)
        {
            var redisConnectionString = configuration["Redis:ConnectionString"] ?? "localhost:6379";

            services.AddStackExchangeRedisCache(options =>
            {
                options.Configuration = redisConnectionString;
            });

            services.AddHealthChecks()
                .AddNpgSql(
                    connectionString: configuration.GetConnectionString("DefaultConnection")!,
                    name: "postgresql")
                .AddRedis(
                    redisConnectionString: redisConnectionString,
                    name: "redis")
                .AddCheck<HangfireHealthCheck>("hangfire");

            return services;
        }

        public static void CheckRedisConnection(this Microsoft.AspNetCore.Builder.WebApplication app)
        {
            var redisConnectionString = app.Configuration["Redis:ConnectionString"];
            if (string.IsNullOrWhiteSpace(redisConnectionString)) return;

            try
            {
                using var connection = StackExchange.Redis.ConnectionMultiplexer.Connect(redisConnectionString);
            }
            catch (Exception ex)
            {
                Microsoft.Extensions.Logging.LoggerExtensions.LogWarning(
                    app.Logger,
                    ex, 
                    "Redis is not running. Distributed caching and SignalR backplane may not work correctly.");
            }
        }
    }
}
