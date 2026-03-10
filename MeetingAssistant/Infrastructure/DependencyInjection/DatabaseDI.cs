using MeetingAssistant.Infrastructure.Persistence.DbContext;
using Microsoft.EntityFrameworkCore;

namespace MeetingAssistant.Infrastructure.DependencyInjection
{
    public static class DatabaseDI
    {
        public static IServiceCollection AddDatabase(this IServiceCollection services, IConfiguration configuration)
        {
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

            return services;
        }
    }
}
