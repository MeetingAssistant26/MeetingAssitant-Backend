using MeetingAssistant.Api.Shared;
using MeetingAssistant.Shared.Abstractions;

namespace MeetingAssistant.Features.LiveSession.Models.PostProcessing
{
    public class PostMeetingProcessingStep : BaseEntity, IHasOrganizationId
    {
        public Guid OrganizationId { get; set; }
        public Guid MeetingId { get; set; }
        public Guid RunId { get; set; }
        public PostMeetingProcessingStepType StepType { get; set; }
        public PostMeetingProcessingStatus Status { get; set; } = PostMeetingProcessingStatus.Pending;
        public int AttemptCount { get; set; }
        public DateTime? LastAttemptAtUtc { get; set; }
        public DateTime? StartedAtUtc { get; set; }
        public DateTime? CompletedAtUtc { get; set; }
        public DateTime? FailedAtUtc { get; set; }
        public string? RelatedHangfireJobId { get; set; }
        public string? ArtifactType { get; set; }
        public Guid? ArtifactId { get; set; }
        public string? ArtifactIdsJson { get; set; }
        public string? ErrorCode { get; set; }
        public string? ErrorMessage { get; set; }

        public PostMeetingProcessingRun? Run { get; set; }
        public ICollection<PostMeetingProcessingEvent> Events { get; set; } = new List<PostMeetingProcessingEvent>();
    }
}
