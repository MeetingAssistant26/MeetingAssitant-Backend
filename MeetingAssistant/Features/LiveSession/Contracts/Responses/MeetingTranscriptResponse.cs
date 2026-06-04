namespace MeetingAssistant.Features.LiveSession.Contracts.Responses
{
    public sealed record MeetingTranscriptResponse(
        Guid MeetingId,
        string Status,
        string? FullText,
        IReadOnlyList<MeetingTranscriptSegmentResponse> Segments,
        string? SttModel,
        DateTime? GeneratedAtUtc,
        string? CompletenessStatus = null,
        bool IsDegraded = false,
        int? ExpectedAudioFragmentCount = null,
        int? TranscribedAudioFragmentCount = null,
        int? RetryableFailedAudioFragmentCount = null,
        int? TerminalFailedAudioFragmentCount = null);

    public sealed record MeetingTranscriptSegmentResponse(
        Guid? ParticipantUserId,
        long StartMs,
        long EndMs,
        string Text,
        double? AvgLogProb,
        int? Version = null,
        Guid? ParticipantAudioTrackId = null,
        long? TrackRelativeStartMs = null,
        long? TrackRelativeEndMs = null,
        long? RoomRelativeStartMs = null,
        long? RoomRelativeEndMs = null,
        DateTime? AbsoluteStartUtc = null,
        DateTime? AbsoluteEndUtc = null,
        string? TimestampOffsetSource = null,
        string? SpeakerRole = null,
        string? SpeakerDisplayName = null,
        string? Source = null,
        string? TraceEventId = null,
        string? SessionId = null,
        string? TurnId = null);
}
