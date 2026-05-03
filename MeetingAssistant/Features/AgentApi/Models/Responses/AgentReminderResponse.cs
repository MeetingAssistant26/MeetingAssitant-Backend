namespace MeetingAssistant.Features.AgentApi.Models.Responses
{
    public sealed record AgentReminderResponse(
        Guid Id,
        string Text,
        string Scope,
        Guid? TargetUserId,
        DateTime ReminderAtUtc,
        string Status);
}
