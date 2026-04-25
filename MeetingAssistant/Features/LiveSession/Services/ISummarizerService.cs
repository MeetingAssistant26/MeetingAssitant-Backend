namespace MeetingAssistant.Features.LiveSession.Services
{
    public interface ISummarizerService
    {
        Task<SummaryResult> SummarizeAsync(string fullTranscript, CancellationToken ct = default);
    }

    public record SummaryResult(
        string SummaryText,
        string Model,
        int? PromptTokens,
        int? CompletionTokens);
}
