using Mapster;
using System.Reflection;

namespace MeetingAssistant.Infrastructure.DependencyInjection
{
    public static class MappingDI
    {
        public static IServiceCollection AddMapping(this IServiceCollection services)
        {
            //add mapster configration
            var config = TypeAdapterConfig.GlobalSettings;
            config.Scan(Assembly.GetExecutingAssembly());

            services.AddSingleton(config);
            services.AddScoped<MapsterMapper.IMapper, MapsterMapper.ServiceMapper>();

            return services;
        }
    }
}
