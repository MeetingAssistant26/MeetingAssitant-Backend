using MeetingAssistant.Api.Shared;
using MeetingAssistant.Features.Meetings.Models;
using MeetingAssistant.Shared.Abstractions;

namespace MeetingAssistant.Features.LiveSession.Models
{
    public class ParticipantAudioFragment : BaseEntity, IHasOrganizationId
    {
        public Guid OrganizationId { get; set; }

        public Guid MeetingId { get; set; }
        public Meeting Meeting { get; set; } = default!;

        public Guid ParticipantUserId { get; set; }

        public Guid? ParticipantAudioTrackId { get; set; }
        public ParticipantAudioTrack? ParticipantAudioTrack { get; set; }

        public string TrackSid { get; set; } = string.Empty;

        public string? EgressId { get; set; }

        public string? StorageObjectKey { get; set; }

        public string? StorageLocation { get; set; }

        public string? BackupStoragePath { get; set; }

        public ParticipantAudioFragmentStatus Status { get; set; } = ParticipantAudioFragmentStatus.Pending;

        public long? SizeBytes { get; set; }

        public double? DurationSeconds { get; set; }

        public DateTime? TrackPublishedAtUtc { get; set; }

        public DateTime? EgressStartedAtUtc { get; set; }

        public DateTime? EgressEndedAtUtc { get; set; }

        public DateTime? BackupStorageAvailableAtUtc { get; set; }

        public DateTime? StorageAvailableAtUtc { get; set; }

        public int EgressStartAttemptCount { get; set; }

        public DateTime? LastEgressStartAttemptAtUtc { get; set; }

        public DateTime? EgressStartLeaseExpiresAtUtc { get; set; }

        public int StorageUploadAttemptCount { get; set; }

        public DateTime? LastStorageUploadAttemptAtUtc { get; set; }

        public DateTime? StorageUploadLeaseExpiresAtUtc { get; set; }

        public DateTime? FailedAtUtc { get; set; }

        public string? FailureCode { get; set; }

        public string? FailureMessage { get; set; }
    }
}
