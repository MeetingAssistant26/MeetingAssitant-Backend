using Microsoft.Extensions.DependencyInjection;
using MeetingAssistant.Features.Meetings.Services;

namespace MeetingAssistant.Features.Meetings
{
    public static class MeetingsDI
    {
        public static IServiceCollection AddMeetingsFeature(this IServiceCollection services)
        {
            services.AddScoped<IMeetingService, MeetingService>();
            services.AddScoped<IParticipantService, ParticipantService>();
            services.AddScoped<IMeetingConflictService, MeetingConflictService>();
            services.AddScoped<IRecurrenceService, RecurrenceService>();
            services.AddScoped<ICalendarService, CalendarService>();

            return services;
        }
    }
}
