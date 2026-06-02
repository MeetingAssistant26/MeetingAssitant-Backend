namespace MeetingAssistant.Features.LiveSession.Services
{
    public interface ISummarizerService
    {
        Task<SummaryResult> SummarizeAsync(string fullTranscript, CancellationToken ct = default);

        Task<SummaryResult> SummarizePersonalizedAsync(
            string fullTranscript,
            string participant,
            string? personalizationContext = null,
            CancellationToken ct = default);
    }

    public record SummaryResult(
        string SummaryText,
        string Model,
        int? PromptTokens,
        int? CompletionTokens,
        string? PromptName = null,
        string? PromptVersion = null);
}
