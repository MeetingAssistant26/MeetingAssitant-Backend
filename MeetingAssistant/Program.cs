using MeetingAssistant.Infrastructure.DependencyInjection;
using MeetingAssistant.Infrastructure.Middleware;
using MeetingAssistant.Infrastructure.SignalR;
using MeetingAssistant.Features.Identity;
using MeetingAssistant.Features.Organizations;
using MeetingAssistant.Features.Meetings;
using Serilog;
using Microsoft.EntityFrameworkCore;

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
                .AddMapping()
                .AddSwaggerServices()
                .AddHangfireServices(builder.Configuration);

            builder.Services.AddSignalR();

            var app = builder.Build();

            app.CheckRedisConnection();

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

            app.UseSecureHangfireDashboard();
            app.RegisterRecurringJobs();

            using (var scope = app.Services.CreateScope())
            {
                var dbContext = scope.ServiceProvider.GetRequiredService<MeetingAssistant.Infrastructure.Persistence.DbContext.ApplicationDbContext>();
                dbContext.Database.Migrate();
            }

            app.Run();
        }
    }
}


