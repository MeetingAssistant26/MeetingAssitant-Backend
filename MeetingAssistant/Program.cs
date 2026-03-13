using Hangfire;
using HangfireBasicAuthenticationFilter;
using Microsoft.EntityFrameworkCore;
using MeetingAssistant.Infrastructure.Persistence.DbContext;
using MeetingAssistant.Infrastructure.DependencyInjection;

namespace MeetingAssistant.Api
{
    public class Program
    {
        public static void Main(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);

            builder.Services.AddDependencies(builder.Configuration);

            var app = builder.Build();

            using (var scope = app.Services.CreateScope())
            {
                var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                dbContext.Database.Migrate();
            }

            if (app.Environment.IsDevelopment())
            {
                app.UseSwagger();
                app.UseSwaggerUI();
            }

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

            app.UseExceptionHandler();
            app.UseCors();
            app.UseAuthentication();
            app.UseAuthorization();

            app.MapControllers();

            app.Run();
        }
    }
}
