using Microsoft.Extensions.DependencyInjection;

namespace MeetingAssistant.Features.DevQa;

public static class DevQaDI
{
    public static IServiceCollection AddDevQaFeature(this IServiceCollection services)
    {
        services.AddSingleton<IQaSttFailureInjectionService, QaSttFailureInjectionService>();
        services.AddScoped<IMeetingTranscriptPreviewService, MeetingTranscriptPreviewService>();
        return services;
    }
}
