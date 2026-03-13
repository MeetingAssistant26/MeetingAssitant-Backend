using MeetingAssistant.Features.Identity;

namespace MeetingAssistant.Infrastructure.DependencyInjection
{
    public static class DependenciesRegistration
    {
        public static IServiceCollection AddDependencies(this IServiceCollection services, IConfiguration configuration)
        {
            services.AddInfrastructure(configuration);
            services.AddDatabase(configuration);
            services.AddAuth(configuration);
            services.AddHangfireServices(configuration);
            services.AddSwaggerServices();
            services.AddMapping();
            services.AddValidation();
            services.AddIdentityFeature();

            return services;
        }
    }
}
