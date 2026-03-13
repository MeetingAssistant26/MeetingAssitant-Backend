using Hangfire;
using HangfireBasicAuthenticationFilter;
using MeetingAssistant.Infrastructure.DependencyInjection;
using MeetingAssistant.Infrastructure.Middleware;
using Serilog;

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
                .AddDatabase(builder.Configuration)
                .AddSwaggerServices()
                .AddHangfireServices(builder.Configuration);

            builder.Services.AddHealthChecks()
                .AddNpgSql(
                    connectionString: builder.Configuration.GetConnectionString("DefaultConnection")!,
                    name: "postgresql")
                .AddRedis(
                    redisConnectionString: builder.Configuration["Redis:ConnectionString"] ?? "localhost:6379",
                    name: "redis")
                .AddCheck<HangfireHealthCheck>("hangfire");

            var app = builder.Build();

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


            app.UseAuthorization();



            app.MapControllers();


            app.Run();
        }
    }
}
