using Hangfire.Server;

namespace MeetingAssistant.Api.Infrastructure.Hangfire;

public sealed class HangfireJobContextFilter(IHangfireJobContextAccessor hangfireJobContextAccessor) : IServerFilter
{
    private readonly IHangfireJobContextAccessor _hangfireJobContextAccessor = hangfireJobContextAccessor;

    public void OnPerforming(PerformingContext filterContext)
    {
        _hangfireJobContextAccessor.CurrentJobId = filterContext.BackgroundJob.Id;
    }

    public void OnPerformed(PerformedContext filterContext)
    {
        _hangfireJobContextAccessor.CurrentJobId = null;
    }
}
