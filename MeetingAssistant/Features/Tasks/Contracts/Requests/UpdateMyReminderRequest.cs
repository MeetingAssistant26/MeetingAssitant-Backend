namespace MeetingAssistant.Features.Tasks.Contracts.Requests
{
    public sealed record UpdateMyReminderRequest(
        string? Text = null,
        DateTime? ReminderAtUtc = null);
}
