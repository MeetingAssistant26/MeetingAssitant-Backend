using Microsoft.Extensions.DependencyInjection;
using MeetingAssistant.Features.Meetings.Jobs;
using MeetingAssistant.Features.Meetings.Services;
using MeetingAssistant.Features.Meetings.Services.TagSuggestions;

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
            services.AddScoped<IMeetingTagSuggestionService, MeetingTagSuggestionService>();
            services.AddScoped<IMeetingTagSuggestionReviewService, MeetingTagSuggestionReviewService>();
            services.AddScoped<SuggestMeetingTagsJob>();

            return services;
        }
    }
}
