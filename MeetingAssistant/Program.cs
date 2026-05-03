using MeetingAssistant.Infrastructure.DependencyInjection;
using MeetingAssistant.Infrastructure.Middleware;
using MeetingAssistant.Infrastructure.SignalR;
using MeetingAssistant.Features.Identity;
using MeetingAssistant.Features.Organizations;
using MeetingAssistant.Features.Meetings;
using MeetingAssistant.Features.Tasks;
using MeetingAssistant.Features.LiveSession;
using MeetingAssistant.Features.LiveSession.Infrastructure;
using MeetingAssistant.Features.DevSeeding;
using MeetingAssistant.Features.AgentApi;
using Serilog;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.RateLimiting;
using System.Globalization;
using System.Threading.RateLimiting;

namespace MeetingAssistant.Api
{
    public class Program
    {
        public static void Main(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);

            builder.Host.UseSerilog((context, loggerConfiguration) => loggerConfiguration
                .ReadFrom.Configuration(context.Configuration)
                .Enrich.FromLogContext()
                .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] [{CorrelationId}] {Message:lj}{NewLine}{Exception}"));

            builder.Services
                .AddInfrastructure(builder.Configuration)
                .AddValidation()
                .AddCachingAndHealthChecks(builder.Configuration)
                .AddDatabase(builder.Configuration)
                .AddAuth(builder.Configuration)
                .AddIdentityFeature()
                .AddOrganizationsFeature()
                .AddMeetingsFeature()
                .AddTasksFeature()
                .AddLiveSessionFeature(builder.Configuration)
                .AddAgentApiFeature()
                .AddMapping()
                .AddSwaggerServices()
                .AddHangfireServices(builder.Configuration);

            builder.Services.AddRateLimiter(options =>
            {
                options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
                options.OnRejected = (context, cancellationToken) =>
                {
                    if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                    {
                        context.HttpContext.Response.Headers.RetryAfter =
                            Math.Ceiling(retryAfter.TotalSeconds).ToString(CultureInfo.InvariantCulture);
                    }

                    return ValueTask.CompletedTask;
                };

                options.AddPolicy("AgentPerMeeting", httpContext =>
                {
                    var meetingPartition = httpContext.User.FindFirst("meetingId")?.Value
                        ?? httpContext.Connection.RemoteIpAddress?.ToString()
                        ?? "agent-anonymous";

                    return RateLimitPartition.GetFixedWindowLimiter(
                        partitionKey: $"agent:{meetingPartition}",
                        factory: _ => new FixedWindowRateLimiterOptions
                        {
                            PermitLimit = 100,
                            Window = TimeSpan.FromMinutes(1),
                            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                            QueueLimit = 0,
                            AutoReplenishment = true
                        });
                });
            });

            builder.Services.Configure<LiveKitOptions>(builder.Configuration.GetSection("LiveKit"));
            builder.Services.Configure<StorageOptions>(builder.Configuration.GetSection("Storage"));
            builder.Services.AddSignalR();

            var app = builder.Build();

            if (!app.Environment.IsEnvironment("Testing"))
            {
                app.CheckRedisConnection();
            }

            if (app.Environment.IsDevelopment())
            {
                app.UseSwagger();
                app.UseSwaggerUI();
            }

            app.UseHttpsRedirection();

            app.UseMiddleware<CorrelationIdMiddleware>();
            app.UseMiddleware<ExceptionHandlingMiddleware>();

            app.UseAuthentication();
            app.UseRateLimiter();
            app.UseAuthorization();
            app.UseCors();

            app.MapControllers();
            app.MapHub<NotificationHub>("/hubs/notifications").RequireAuthorization();
            app.MapHealthChecks("/healthz");

            if (!app.Environment.IsEnvironment("Testing"))
            {
                app.UseSecureHangfireDashboard();
                app.RegisterRecurringJobs();

                using (var scope = app.Services.CreateScope())
                {
                    var dbContext = scope.ServiceProvider.GetRequiredService<MeetingAssistant.Infrastructure.Persistence.DbContext.ApplicationDbContext>();
                    dbContext.Database.Migrate();

                    if (app.Environment.IsDevelopment())
                    {
                        var seeder = scope.ServiceProvider.GetRequiredService<DevDbSeeder>();
                        seeder.SeedAsync().GetAwaiter().GetResult();
                    }
                }
            }

            app.Run();
        }
    }
}




