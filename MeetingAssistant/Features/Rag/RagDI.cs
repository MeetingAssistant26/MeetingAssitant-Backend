using MeetingAssistant.Features.Rag.Services;

namespace MeetingAssistant.Features.Rag
{
    public static class RagDI
    {
        public static IServiceCollection AddRagFeature(this IServiceCollection services)
        {
            services.AddScoped<IKnowledgeRetrievalService, KnowledgeRetrievalService>();
            return services;
        }
    }
}
