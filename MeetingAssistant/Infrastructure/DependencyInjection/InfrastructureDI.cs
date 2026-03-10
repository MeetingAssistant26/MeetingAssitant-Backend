using MeetingAssistant.Api.Infrastructure.Configuration;
using MeetingAssistant.Api.Infrastructure.Services;
using MeetingAssistant.Shared.Errors;
using MeetingAssistant.Shared.Settings;
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
            services.AddExceptionHandler<GlobalExceptionHandler>();

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

            return services;
        }
    }
}
