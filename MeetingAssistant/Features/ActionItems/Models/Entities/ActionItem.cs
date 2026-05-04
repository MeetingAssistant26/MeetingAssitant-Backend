using MeetingAssistant.Api.Shared;
using MeetingAssistant.Features.ActionItems.Models.Enums;
using MeetingAssistant.Features.Identity.Entites;
using MeetingAssistant.Features.Meetings.Models;
using MeetingAssistant.Shared.Abstractions;

namespace MeetingAssistant.Features.ActionItems.Models.Entities
{
    public class ActionItem : BaseEntity, IHasOrganizationId
    {
        public Guid OrganizationId { get; set; }

        public Guid MeetingId { get; set; }
        public Meeting Meeting { get; set; } = default!;

        public string Title { get; set; } = string.Empty;

        public string? Description { get; set; }

        public Guid? AssignedToParticipantId { get; set; }
        public MeetingParticipant? AssignedToParticipant { get; set; }

        public Guid? AssignedToUserId { get; set; }
        public ApplicationUser? AssignedToUser { get; set; }

        public DateTime? DueDateUtc { get; set; }

        public ActionItemStatus Status { get; set; } = ActionItemStatus.PendingReview;

        public string? ExternalTaskId { get; set; }

        public string? ExternalTaskUrl { get; set; }

        public ExternalProvider? ExternalProvider { get; set; }

        public string? SyncMissingAssigneeReason { get; set; }

        public DateTime ExtractedAtUtc { get; set; }

        public DateTime? SyncedAtUtc { get; set; }

        public byte[] RowVersion { get; set; } = Array.Empty<byte>();
    }
}
