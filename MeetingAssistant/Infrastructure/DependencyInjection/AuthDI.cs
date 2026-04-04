using MeetingAssistant.Features.Identity.Entites;
using MeetingAssistant.Api.Infrastructure.Configuration;
using MeetingAssistant.Infrastructure.Persistence.DbContext;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.IdentityModel.Tokens;
using Microsoft.AspNetCore.Authorization;
using System.Text;

namespace MeetingAssistant.Infrastructure.DependencyInjection
{
    public static class AuthDI
    {
        public static IServiceCollection AddAuth(this IServiceCollection services, IConfiguration configuration)
        {
            //add Auth configration
            services.AddIdentityCore<ApplicationUser>()
                    .AddRoles<IdentityRole<Guid>>()
                    .AddEntityFrameworkStores<ApplicationDbContext>()
                    .AddDefaultTokenProviders()
                    .AddSignInManager();

            services.AddOptions<JwtSettings>()
                .BindConfiguration("Jwt")
                .Validate(settings =>
                    !string.IsNullOrWhiteSpace(settings.SigningKey) &&
                    settings.SigningKey.Length >= 32 &&
                    !string.IsNullOrWhiteSpace(settings.Issuer) &&
                    !string.IsNullOrWhiteSpace(settings.Audience) &&
                    settings.TokenExpiryMinutes > 0 &&
                    settings.RefreshTokenExpiryDays > 0,
                    "Jwt settings must include SigningKey (>= 32 chars), Issuer, Audience, TokenExpiryMinutes > 0, and RefreshTokenExpiryDays > 0.")
                .ValidateOnStart();

            var jwtSettings = configuration.GetSection("Jwt").Get<JwtSettings>();
            if (jwtSettings is null || string.IsNullOrWhiteSpace(jwtSettings.SigningKey))
            {
                throw new InvalidOperationException("Missing Jwt configuration. Set Jwt:SigningKey via user-secrets or environment variables.");
            }

            var issuerSigningKeys = new List<SecurityKey>
            {
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSettings.SigningKey!))
            };

            if (jwtSettings.IssuerSigningKeys != null && jwtSettings.IssuerSigningKeys.Any())
            {
                foreach (var key in jwtSettings.IssuerSigningKeys)
                {
                    if (!string.IsNullOrWhiteSpace(key))
                    {
                        issuerSigningKeys.Add(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key)));
                    }
                }
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
                        IssuerSigningKeys = issuerSigningKeys,
                        ClockSkew = TimeSpan.Zero
                 };

            });

            services.Configure<IdentityOptions>(options =>
            {
                // Password rules are enforced by FluentValidation via RegexPatterns.Password at the endpoint level
                options.Password.RequireLowercase = false;
                options.Password.RequireUppercase = false;
                options.Password.RequireDigit = false;
                options.Password.RequireNonAlphanumeric = false;
                options.Password.RequiredLength = 8;

                options.Lockout.MaxFailedAccessAttempts = 5;
                options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
                options.Lockout.AllowedForNewUsers = true;

                options.SignIn.RequireConfirmedEmail = true;
                options.User.RequireUniqueEmail = true;
            });

            services.AddAuthorization(options =>
            {
                options.FallbackPolicy = new AuthorizationPolicyBuilder()
                    .RequireAuthenticatedUser()
                    .Build();

                options.AddPolicy("RequireOrgAdmin", policy =>
                    policy.RequireAuthenticatedUser()
                          .RequireClaim("org_role", "Admin"));

                options.AddPolicy("RequireOrgMember", policy =>
                    policy.RequireAuthenticatedUser()
                          .RequireClaim("org_role", "Admin", "Member"));

                options.AddPolicy("RequireOrgAccess", policy =>
                    policy.RequireAuthenticatedUser()
                          .RequireClaim("org_role", "Admin", "Member", "Guest"));
            });

            return services;
        }
    }
}
