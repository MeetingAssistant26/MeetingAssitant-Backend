using MeetingAssistant.Api.Shared;
using MeetingAssistant.Features.Tasks.Models.Enums;
using MeetingAssistant.Shared.Abstractions;

namespace MeetingAssistant.Features.Tasks.Models.Entities
{
    public class Reminder : BaseEntity, IHasOrganizationId
    {
        public Guid OrganizationId { get; set; }

        public string Text { get; set; } = string.Empty;

        public ReminderScope Scope { get; set; } = ReminderScope.Personal;

        public ReminderChannel Channel { get; set; } = ReminderChannel.User;

        public Guid? CreatedByUserId { get; set; }

        public Guid? TargetUserId { get; set; }

        public Guid? MeetingId { get; set; }

        public DateTime ReminderAtUtc { get; set; }

        public ReminderStatus Status { get; set; } = ReminderStatus.Active;

        public DateTime? DeliveredAtUtc { get; set; }

        public string? OriginalText { get; set; }
    }
}
