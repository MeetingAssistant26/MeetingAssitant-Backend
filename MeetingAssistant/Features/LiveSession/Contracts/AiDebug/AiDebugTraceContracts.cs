using System.Text.Json;

namespace MeetingAssistant.Features.LiveSession.Contracts.AiDebug
{
    public sealed record AiDebugTraceIngestRequest(
        string SessionId,
        string TurnId,
        int Sequence,
        string EventType,
        DateTime OccurredAtUtc,
        string? ParticipantIdentity,
        string? State,
        AiDebugTraceStepDto? Step,
        AiDebugTraceErrorDto? Error);

    public sealed record AiDebugTraceStepDto(
        string? Type,
        string? Provider,
        string? Endpoint,
        string? Model,
        string? Voice,
        int? DurationMs,
        int? PromptTokens,
        int? CompletionTokens,
        int? TotalTokens,
        int? CharactersCount,
        int? AudioDurationMs,
        JsonElement? RequestPayload,
        JsonElement? ResponsePayload,
        string? Text);

    public sealed record AiDebugTraceErrorDto(
        string? Message,
        string? Type);

    public sealed record AiDebugTraceResponse(
        bool Enabled,
        bool PersistPayloads,
        IReadOnlyList<AiDebugTurnResponse> Turns);

    public sealed record AiDebugTurnResponse(
        string SessionId,
        string TurnId,
        string? ParticipantIdentity,
        DateTime StartedAtUtc,
        DateTime? CompletedAtUtc,
        string Status,
        int? TotalDurationMs,
        IReadOnlyList<AiDebugTraceEventResponse> Events);

    public sealed record AiDebugTraceEventResponse(
        Guid Id,
        int Sequence,
        string EventType,
        DateTime OccurredAtUtc,
        string? State,
        AiDebugTraceStepDto? Step,
        AiDebugTraceErrorDto? Error);
}
