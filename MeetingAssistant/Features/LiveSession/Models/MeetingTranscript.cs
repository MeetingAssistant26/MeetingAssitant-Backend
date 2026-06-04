using MeetingAssistant.Api.Shared;
using MeetingAssistant.Features.Meetings.Models;
using MeetingAssistant.Shared.Abstractions;

namespace MeetingAssistant.Features.LiveSession.Models
{
    public class MeetingTranscript : BaseEntity, IHasOrganizationId
    {
        public Guid MeetingId { get; set; }
        public Meeting Meeting { get; set; } = default!;

        public Guid OrganizationId { get; set; }

        public string FullText { get; set; } = string.Empty;

        public string SegmentsJson { get; set; } = "[]";

        public string SttModel { get; set; } = string.Empty;

        public DateTime GeneratedAtUtc { get; set; }

        public MeetingTranscriptCompletenessStatus CompletenessStatus { get; set; } =
            MeetingTranscriptCompletenessStatus.Complete;

        public int ExpectedAudioFragmentCount { get; set; }

        public int TranscribedAudioFragmentCount { get; set; }

        public int RetryableFailedAudioFragmentCount { get; set; }

        public int TerminalFailedAudioFragmentCount { get; set; }

        public string MissingAudioFragmentIdsJson { get; set; } = "[]";

        public string WarningsJson { get; set; } = "[]";
    }
}
