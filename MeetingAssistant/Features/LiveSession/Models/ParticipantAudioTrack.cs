using MeetingAssistant.Api.Shared;
using MeetingAssistant.Features.Meetings.Models;
using MeetingAssistant.Shared.Abstractions;

namespace MeetingAssistant.Features.LiveSession.Models
{
    public class ParticipantAudioTrack : BaseEntity, IHasOrganizationId
    {
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
