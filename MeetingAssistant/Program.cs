using Hangfire;
using HangfireBasicAuthenticationFilter;
using MeetingAssistant.Api.Infrastructure.Configuration;
using MeetingAssistant.Infrastructure.DependencyInjection;
using MeetingAssistant.Infrastructure.Middleware;
using MeetingAssistant.Infrastructure.SignalR;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Serilog;
using StackExchange.Redis;
using System.Text;

namespace MeetingAssistant.Api
{
    public class Program
    {
        public static void Main(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);

            var redisConnectionString = builder.Configuration["Redis:ConnectionString"];
            if (string.IsNullOrWhiteSpace(redisConnectionString))
            {
                throw new InvalidOperationException("Missing Redis:ConnectionString configuration. Configure it via user-secrets or environment variables.");
            }

            builder.Services.Configure<LiveKitSettings>(builder.Configuration.GetSection("LiveKit"));
            builder.Services.AddStackExchangeRedisCache(options =>
            {
                options.Configuration = redisConnectionString;
            });

            builder.Services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
            {
                var jwtSettings = builder.Configuration.GetSection("Jwt").Get<JwtSettings>();
                if (jwtSettings is null || string.IsNullOrWhiteSpace(jwtSettings.SigningKey))
                {
                    throw new InvalidOperationException("Missing Jwt configuration. Configure Jwt settings via user-secrets or environment variables.");
                }

                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuerSigningKey = true,
                    ValidateAudience = true,
                    ValidateIssuer = true,
                    ValidateLifetime = true,
                    ValidIssuer = jwtSettings.Issuer,
                    ValidAudience = jwtSettings.Audience,
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSettings.SigningKey)),
                    ClockSkew = TimeSpan.Zero
                };
            });

            builder.Host.UseSerilog((context, loggerConfiguration) => loggerConfiguration
                .ReadFrom.Configuration(context.Configuration)
                .Enrich.FromLogContext()
                .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] [{CorrelationId}] {Message:lj}{NewLine}{Exception}"));

            builder.Services
                .AddInfrastructure(builder.Configuration)
                .AddDatabase(builder.Configuration)
                .AddAuth(builder.Configuration)
                .AddSwaggerServices()
                .AddHangfireServices(builder.Configuration);

            builder.Services.AddSignalR();

            builder.Services.AddHealthChecks()
                .AddNpgSql(
                    connectionString: builder.Configuration.GetConnectionString("DefaultConnection")!,
                    name: "postgresql")
                .AddRedis(
                    redisConnectionString: redisConnectionString,
                    name: "redis")
                .AddCheck<HangfireHealthCheck>("hangfire");

            var app = builder.Build();

            EnsureRedisConnectionIsAvailable(redisConnectionString);

            if (app.Environment.IsDevelopment())
            {
                app.UseSwagger();
                app.UseSwaggerUI();
            }

            app.UseHttpsRedirection();

            app.UseMiddleware<CorrelationIdMiddleware>();
            app.UseMiddleware<ExceptionHandlingMiddleware>();

            app.UseAuthentication();
            app.UseAuthorization();
            app.UseCors();

            app.MapControllers();
            app.MapHub<NotificationHub>("/hubs/notifications").RequireAuthorization();
            app.MapHealthChecks("/healthz");

            app.UseHangfireDashboard("/jobs", new DashboardOptions
            {
                Authorization =
                [
                    new HangfireCustomBasicAuthenticationFilter
                    {
                        User = app.Configuration["HangfireSettings:DashboardUsername"],
                        Pass = app.Configuration["HangfireSettings:DashboardPassword"]
                    }
                ]
            });

            app.Run();
        }

        private static void EnsureRedisConnectionIsAvailable(string redisConnectionString)
        {
            try
            {
                using var connection = ConnectionMultiplexer.Connect(redisConnectionString);
                if (!connection.IsConnected)
                {
                    throw new InvalidOperationException(
                        "Redis dependency is mandatory and could not be reached. Ensure Redis is running and Redis:ConnectionString is correct.");
                }
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    "Redis dependency is mandatory and could not be reached. Ensure Redis is running and Redis:ConnectionString is correct.", ex);
            }
        }
    }
}
