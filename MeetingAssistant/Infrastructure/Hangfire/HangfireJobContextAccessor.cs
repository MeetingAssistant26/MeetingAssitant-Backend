namespace MeetingAssistant.Api.Infrastructure.Hangfire;

public sealed class HangfireJobContextAccessor : IHangfireJobContextAccessor
{
    private static readonly AsyncLocal<string?> Current = new();

    public string? CurrentJobId
    {
        get => Current.Value;
        set => Current.Value = value;
    }
}
