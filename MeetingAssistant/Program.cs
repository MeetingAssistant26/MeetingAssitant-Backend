using Hangfire;
using HangfireBasicAuthenticationFilter;
using Microsoft.EntityFrameworkCore;
using MyMeetingAssistant;

namespace MeetingAssistant.Api
{
    public class Program
    {
        public static void Main(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);


            // Add services to the container.


            builder.Services.AddDependencies(builder.Configuration);

            var app = builder.Build();

            // Configure the HTTP request pipeline.
            if (app.Environment.IsDevelopment())
            {
                app.UseSwagger();
                app.UseSwaggerUI();
            }

            app.UseHttpsRedirection();

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

            app.UseAuthorization();

            app.UseAuthorization();



            app.MapControllers();


            app.Run();
        }
    }
}
