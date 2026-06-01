using MeetingAssistant.Api.Shared;
using MeetingAssistant.Features.Meetings.Models;
using MeetingAssistant.Shared.Abstractions;

namespace MeetingAssistant.Features.LiveSession.Models
{
    public class ParticipantAudioTrack : BaseEntity, IHasOrganizationId
    {
        // ParticipantAudioTrack is intentionally the participant-level aggregate consumed by
        // transcript/summary jobs, not a raw LiveKit track fragment. Raw track_published and
        // egress_ended fragments remain auditable as SessionEvent payloads until a dedicated
        // fragment table is introduced; this preserves the existing schema/unique index while
        // keeping mute/unmute/rejoin artifacts attached to one logical meeting participant.
        public Guid MeetingId { get; set; }
        public Meeting Meeting { get; set; } = default!;

        public Guid OrganizationId { get; set; }

        public Guid ParticipantUserId { get; set; }

        public ParticipantAudioTrackStatus Status { get; set; } = ParticipantAudioTrackStatus.Pending;

        public string? StorageObjectKey { get; set; }

        public double? DurationSeconds { get; set; }

        public long? SizeBytes { get; set; }
    }
}
