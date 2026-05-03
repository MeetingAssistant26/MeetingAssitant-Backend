using MeetingAssistant.Features.Tasks.Models.Enums;

namespace MeetingAssistant.Features.Tasks.Contracts.Responses
{
    public sealed record ReminderResponse(
        Guid Id,
        string Text,
        ReminderScope Scope,
        ReminderChannel Channel,
        Guid? CreatedByUserId,
        Guid? TargetUserId,
        Guid? MeetingId,
        DateTime ReminderAtUtc,
        ReminderStatus Status,
        DateTime? DeliveredAtUtc,
        DateTime CreatedAtUtc,
        DateTime UpdatedAtUtc);
}
