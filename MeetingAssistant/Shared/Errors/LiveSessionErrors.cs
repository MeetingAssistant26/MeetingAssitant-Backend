using MeetingAssistant.Shared.Abstractions;
using Microsoft.AspNetCore.Http;

namespace MeetingAssistant.Shared.Errors
{
    public static class LiveSessionErrors
    {
        public static readonly Error NotAParticipant = new(
            "LiveSession.NotAParticipant",
            "You are not a participant of this meeting.",
            StatusCodes.Status403Forbidden);

        public static readonly Error MeetingNotJoinable = new(
            "LiveSession.MeetingNotJoinable",
            "This meeting cannot be joined in its current state.",
            StatusCodes.Status409Conflict);

        public static readonly Error InvalidWebhookSignature = new(
            "LiveSession.InvalidWebhookSignature",
            "The webhook signature is invalid.",
            StatusCodes.Status401Unauthorized);

        public static readonly Error LiveKitCallFailed = new(
            "LiveSession.LiveKitCallFailed",
            "Unable to issue a LiveKit credential at this time.",
            StatusCodes.Status502BadGateway);

        public static readonly Error MeetingNotFound = new(
            "LiveSession.MeetingNotFound",
            "The specified meeting was not found.",
            StatusCodes.Status404NotFound);

        public static readonly Error RecordingDownloadFailed = new(
            "LiveSession.RecordingDownloadFailed",
            "The recording could not be downloaded.",
            StatusCodes.Status500InternalServerError);
    }
}
