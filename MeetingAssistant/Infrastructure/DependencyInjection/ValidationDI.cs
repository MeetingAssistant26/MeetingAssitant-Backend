using FluentValidation;
using SharpGrip.FluentValidation.AutoValidation.Mvc.Extensions;
using System.Reflection;

namespace MeetingAssistant.Infrastructure.DependencyInjection
{
    public static class ValidationDI
    {
        public static IServiceCollection AddValidation(this IServiceCollection services)
        {
            // Fluent Validation Configuration
            services.AddFluentValidationAutoValidation()
                    .AddValidatorsFromAssembly(Assembly.GetExecutingAssembly());

            return services;
        }
    }
}
