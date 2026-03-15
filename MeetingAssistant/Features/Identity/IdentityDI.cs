using MeetingAssistant.Features.Identity.Services;
using Microsoft.AspNetCore.Identity.UI.Services;

namespace MeetingAssistant.Features.Identity
{
    public static class IdentityDI
    {
        public static IServiceCollection AddIdentityFeature(this IServiceCollection services)
        {
            services.AddScoped<IAuthService, AuthService>();
            services.AddScoped<ITokenService, TokenService>();
            // services.AddScoped<IProfileService, ProfileService>(); // Will be implemented in Phase further steps
            services.AddScoped<IEmailSender, EmailService>();

            return services;
        }
    }
}
