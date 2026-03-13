using MeetingAssistant.Infrastructure.AI.DTOs;

namespace MeetingAssistant.Infrastructure.AI
{
    public interface ILLMService
    {
        Task<LLMResponse> CompleteAsync(LLMRequest request, CancellationToken cancellationToken);
        Task<T> CompleteWithJsonAsync<T>(LLMRequest request, CancellationToken cancellationToken);
    }
}
