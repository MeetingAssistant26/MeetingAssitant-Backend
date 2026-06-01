namespace MeetingAssistant.Features.LiveSession.Contracts.Responses
{
    public sealed record MeetingTranscriptResponse(
        Guid MeetingId,
        string Status,
        string? FullText,
        IReadOnlyList<MeetingTranscriptSegmentResponse> Segments,
        string? SttModel,
        DateTime? GeneratedAtUtc);

    public sealed record MeetingTranscriptSegmentResponse(
        Guid ParticipantUserId,
        long StartMs,
        long EndMs,
        string Text,
        double? AvgLogProb);
}
