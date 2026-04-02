using FluentValidation;
using MeetingAssistant.Infrastructure.Validation;
using SharpGrip.FluentValidation.AutoValidation.Mvc.Extensions;
using System.Reflection;

namespace MeetingAssistant.Infrastructure.DependencyInjection
{
    public static class ValidationDI
    {
        public static IServiceCollection AddValidation(this IServiceCollection services)
        {
            services.AddFluentValidationAutoValidation(configuration =>
            {
                configuration.DisableBuiltInModelValidation = true;
                configuration.OverrideDefaultResultFactoryWith<ValidationResultFactory>();
            });

            services.AddValidatorsFromAssembly(Assembly.GetExecutingAssembly());

            return services;
        }
    }
}
