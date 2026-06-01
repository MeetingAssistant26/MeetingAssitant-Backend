using MeetingAssistant.Features.Rag.Services;
using MeetingAssistant.Features.Rag.Jobs;

namespace MeetingAssistant.Features.Rag
{
    public static class RagDI
    {
        public static IServiceCollection AddRagFeature(this IServiceCollection services)
        {
            services.AddScoped<IKnowledgeRetrievalService, KnowledgeRetrievalService>();
            services.AddScoped<IReindexMeetingKnowledgeService, ReindexMeetingKnowledgeService>();
            services.AddScoped<ReindexMeetingKnowledgeJob>();
            return services;
        }
    }
}
