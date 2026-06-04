using MeetingAssistant.Features.LiveSession.Models;

namespace MeetingAssistant.Features.LiveSession.Services
{
    public static class MeetingTranscriptCompletenessGuard
    {
        public const string IncompleteErrorCode = "transcript_incomplete";

        public static bool IsCompleteForDownstream(MeetingTranscript transcript)
            => transcript.CompletenessStatus == MeetingTranscriptCompletenessStatus.Complete;

        public static string BuildIncompleteMessage(MeetingTranscript transcript)
        {
            var missingCount = transcript.RetryableFailedAudioFragmentCount
                               + transcript.TerminalFailedAudioFragmentCount;

            return missingCount > 0
                ? $"Transcript is incomplete ({transcript.CompletenessStatus}); {missingCount} audio fragment(s) were not fully transcribed."
                : $"Transcript is incomplete ({transcript.CompletenessStatus}).";
        }
    }
}
