using MeetingAssistant.Api.Shared;
using MeetingAssistant.Shared.Abstractions;

namespace MeetingAssistant.Features.LiveSession.Models.PostProcessing
{
    public class PostMeetingProcessingEvent : BaseEntity, IHasOrganizationId
    {
        public Guid OrganizationId { get; set; }
        public Guid MeetingId { get; set; }
        public Guid RunId { get; set; }
        public Guid? StepId { get; set; }
        public PostMeetingProcessingStepType? StepType { get; set; }
        public PostMeetingProcessingEventType EventType { get; set; }
        public PostMeetingProcessingStatus? Status { get; set; }
        public DateTime OccurredAtUtc { get; set; } = DateTime.UtcNow;
        public string? RelatedHangfireJobId { get; set; }
        public string? Message { get; set; }
        public string? ArtifactType { get; set; }
        public Guid? ArtifactId { get; set; }
        public string? ArtifactIdsJson { get; set; }
        public string? ErrorCode { get; set; }
        public string? ErrorMessage { get; set; }
        public string? MetadataJson { get; set; }

        public PostMeetingProcessingRun? Run { get; set; }
        public PostMeetingProcessingStep? Step { get; set; }
    }
}
