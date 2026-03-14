using MeetingAssistant.Features.Authentication.Authentication;
using MeetingAssistant.Features.Identity.Entites;
using MeetingAssistant.Api.Infrastructure.Configuration;
using MeetingAssistant.Infrastructure.Persistence.DbContext;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.IdentityModel.Tokens;
using System.Text;

namespace MeetingAssistant.Infrastructure.DependencyInjection
{
    public static class AuthDI
    {
        public static IServiceCollection AddAuth(this IServiceCollection services, IConfiguration configuration)
        {
            //add Auth configration
            services.AddSingleton<IJwtProvider, JwtProvider>();
            services.AddIdentity<ApplicationUser,IdentityRole>()
                    .AddEntityFrameworkStores<ApplicationDbContext>()
                    .AddDefaultTokenProviders();

            services.AddOptions<JwtSettings>()
                .BindConfiguration("Jwt")
                .Validate(settings =>
                    !string.IsNullOrWhiteSpace(settings.SigningKey) &&
                    !string.IsNullOrWhiteSpace(settings.Issuer) &&
                    !string.IsNullOrWhiteSpace(settings.Audience) &&
                    settings.TokenExpiryMinutes > 0,
                    "Jwt settings must include SigningKey, Issuer, Audience, and TokenExpiryMinutes > 0.")
                .ValidateOnStart();

            var jwtSettings = configuration.GetSection("Jwt").Get<JwtSettings>();
            if (jwtSettings is null || string.IsNullOrWhiteSpace(jwtSettings.SigningKey))
            {
                throw new InvalidOperationException("Missing Jwt configuration. Set Jwt:SigningKey via user-secrets or environment variables.");
            }

            services.AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
                options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;


            })
                .AddJwtBearer(options =>
            {
                 options.SaveToken = true;
                 options.TokenValidationParameters = new TokenValidationParameters
                    {
                        ValidateIssuerSigningKey = true,
                        ValidateAudience = true,
                        ValidateIssuer = true,
                        ValidateLifetime = true,
                        ValidIssuer = jwtSettings.Issuer,
                        ValidAudience = jwtSettings.Audience,
                        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSettings.SigningKey!)),
                        ClockSkew = TimeSpan.Zero
                 };

            });

            services.Configure<IdentityOptions>(options =>
            {
                options.Password.RequiredLength = 8;
                options.SignIn.RequireConfirmedEmail = true;
                options.User.RequireUniqueEmail = true;

            } );

            return services;
        }
    }
}
