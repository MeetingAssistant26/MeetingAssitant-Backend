namespace MeetingAssistant.Features.LiveSession.Contracts.Responses
{
    public sealed record PersonalizedMeetingSummaryResponse(
        Guid MeetingId,
        Guid? UserId,
        Guid? MeetingParticipantId,
        string Status,
        string? SummaryText,
        string? LlmModel,
        int? PromptTokens,
        int? CompletionTokens,
        DateTime? GeneratedAtUtc,
        string? TargetDisplayName,
        string? PromptName,
        string? PromptVersion);
}
