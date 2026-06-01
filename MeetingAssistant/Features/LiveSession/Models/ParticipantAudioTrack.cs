using MeetingAssistant.Api.Shared;
using MeetingAssistant.Features.Meetings.Models;
using MeetingAssistant.Shared.Abstractions;

namespace MeetingAssistant.Features.LiveSession.Models
{
    public class ParticipantAudioTrack : BaseEntity, IHasOrganizationId
    {
        // ParticipantAudioTrack is intentionally a participant-level aggregate for compatibility.
        // ParticipantAudioFragments are the transcript/STT source of truth so mute/unmute/rejoin
        // fragments are not overwritten by this aggregate row.
        public Guid MeetingId { get; set; }
        public Meeting Meeting { get; set; } = default!;

        public Guid OrganizationId { get; set; }

        public Guid ParticipantUserId { get; set; }

        public ParticipantAudioTrackStatus Status { get; set; } = ParticipantAudioTrackStatus.Pending;

        public string? StorageObjectKey { get; set; }

        public double? DurationSeconds { get; set; }

        public long? SizeBytes { get; set; }

        public ICollection<ParticipantAudioFragment> Fragments { get; set; } = new List<ParticipantAudioFragment>();
    }
}
