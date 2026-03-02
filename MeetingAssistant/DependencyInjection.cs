
using FluentValidation;
using Hangfire;
using Hangfire.PostgreSql;
using Mapster;
using MeetingAssistant.Authentication;
using MeetingAssistant.Entities;
using MeetingAssistant.Errors;
using MeetingAssistant.Persistence;
using MeetingAssistant.Services;
using MeetingAssistant.Settings;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.UI.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using SharpGrip.FluentValidation.AutoValidation.Mvc.Extensions;
using System.Reflection;
using System.Text;

namespace MyMeetingAssistant
{
    public static class DependencyInjection
    {
        public static IServiceCollection AddDependencies(this IServiceCollection services,IConfiguration configuration)
        {
            services.AddControllers();

            services.AddCors(options =>
                options.AddDefaultPolicy(builder =>
                builder.AllowAnyOrigin()
                .AllowAnyMethod()
                .AllowAnyHeader()
                )
            );
                
            
            //Add DbContext Configuration
            var connectionString = configuration.GetConnectionString("DefaultConnection");
            services.AddDbContext<ApplicationDbContext>(options =>
                  options.UseNpgsql(
                     configuration.GetConnectionString("DefaultConnection"),
                     npgsqlOptions =>
                     {
                         npgsqlOptions.EnableRetryOnFailure(
                             maxRetryCount: 5,
                             maxRetryDelay: TimeSpan.FromSeconds(10),
                             errorCodesToAdd: null);
                     }));

            services.AddSwaggerDependencies()
                    .Addmapsterconfig()
                    .AddFluentValidconfig()
                    .AddAuthconfig(configuration);
            
            services.AddScoped<IAuthService, AuthService>();    
            services.AddScoped<IEmailSender, EmailService>();


            services.AddProblemDetails();
            services.AddExceptionHandler<GlobalExceptionHandler>();
            services.AddHangfireconfig(configuration); 

            services.AddHttpContextAccessor();
            services.Configure<MailSettings>(configuration.GetSection(nameof(MailSettings)));

            

            return services;

        }
        public static IServiceCollection AddSwaggerDependencies(this IServiceCollection services)
        {
            // Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
            services.AddEndpointsApiExplorer();
            services.AddSwaggerGen();

            return services;

        }
        public static IServiceCollection AddFluentValidconfig(this IServiceCollection services)
        {
            // Fluent Validation Configuration
            services.AddFluentValidationAutoValidation()
                    .AddValidatorsFromAssembly(Assembly.GetExecutingAssembly());

            return services;

        }
        public static IServiceCollection Addmapsterconfig(this IServiceCollection services)
        {
            //add mapster configration
            var config = TypeAdapterConfig.GlobalSettings;
            config.Scan(Assembly.GetExecutingAssembly());

            return services;

        }
        public static IServiceCollection AddAuthconfig(this IServiceCollection services, IConfiguration configuration)
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


        public static IServiceCollection AddHangfireconfig(this IServiceCollection services, IConfiguration configuration)
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
                    });
            });


            // Add the processing server as IHostedService
            services.AddHangfireServer();

            return services;

        }


    }
}
