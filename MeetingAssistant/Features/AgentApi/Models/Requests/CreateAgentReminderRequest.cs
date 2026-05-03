namespace MeetingAssistant.Features.AgentApi.Models.Requests
{
    public sealed record CreateAgentReminderRequest(
        string Text,
        string Scope,
        Guid? TargetUserId,
        DateTime ReminderAtUtc,
        Guid? CreatedByUserId);
}
