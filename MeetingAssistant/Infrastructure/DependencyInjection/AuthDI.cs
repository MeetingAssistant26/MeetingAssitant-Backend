using MeetingAssistant.Features.Authentication.Authentication;
using MeetingAssistant.Features.Identity.Entites;
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

         
            //DI for jwtoptions
            services.AddOptions<JwtOptions>()
                    .BindConfiguration(nameof(JwtOptions))
                    .ValidateDataAnnotations()
                    .ValidateOnStart();
            



            var JwtSettings=configuration.GetSection(nameof(JwtOptions)).Get<JwtOptions>();

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
                        ValidIssuer = JwtSettings?.Issuer,
                        ValidAudience= JwtSettings?.Audience,
                        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(JwtSettings!.Key))

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
