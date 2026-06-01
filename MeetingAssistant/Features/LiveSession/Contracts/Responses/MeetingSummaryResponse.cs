namespace MeetingAssistant.Features.LiveSession.Contracts.Responses
{
    public sealed record MeetingSummaryResponse(
        Guid MeetingId,
        string Status,
        string? SummaryText,
        string? LlmModel,
        int? PromptTokens,
        int? CompletionTokens,
        DateTime? GeneratedAtUtc);
}
