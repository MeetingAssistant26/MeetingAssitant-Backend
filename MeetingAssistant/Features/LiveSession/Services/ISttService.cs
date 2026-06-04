namespace MeetingAssistant.Features.LiveSession.Services
{
    public interface ISttService
    {
        Task<TrackTranscriptionResult> TranscribeTrackAsync(
            Guid? participantUserId,
            string storageObjectKey,
            CancellationToken ct = default);
    }

    public record TrackTranscriptionResult(
        string Model,
        IReadOnlyList<TranscriptSegment> Segments);

    public record TranscriptSegment(
        Guid? ParticipantUserId,
        long StartMs,
        long EndMs,
        string Text,
        double? AvgLogProb);
}
