namespace MeetingAssistant.Features.Tasks.Contracts.Requests
{
    public sealed record CreateMyReminderRequest(
        string Text,
        DateTime ReminderAtUtc);
}
