using MeetingAssistant.Api.Shared;
using MeetingAssistant.Features.Meetings.Models;
using MeetingAssistant.Shared.Abstractions;

namespace MeetingAssistant.Features.LiveSession.Models.PostProcessing
{
    public class PostMeetingProcessingRun : BaseEntity, IHasOrganizationId
    {
        public Guid OrganizationId { get; set; }
        public Guid MeetingId { get; set; }
        public Guid PipelineGenerationId { get; set; } = Guid.NewGuid();
        public PostMeetingProcessingStatus Status { get; set; } = PostMeetingProcessingStatus.Pending;
        public int AttemptCount { get; set; }
        public DateTime? StartedAtUtc { get; set; }
        public DateTime? CompletedAtUtc { get; set; }
        public DateTime? FailedAtUtc { get; set; }
        public string? RelatedHangfireJobId { get; set; }
        public string? ErrorCode { get; set; }
        public string? ErrorMessage { get; set; }

        public Meeting? Meeting { get; set; }
        public ICollection<PostMeetingProcessingStep> Steps { get; set; } = new List<PostMeetingProcessingStep>();
        public ICollection<PostMeetingProcessingEvent> Events { get; set; } = new List<PostMeetingProcessingEvent>();
    }
}
