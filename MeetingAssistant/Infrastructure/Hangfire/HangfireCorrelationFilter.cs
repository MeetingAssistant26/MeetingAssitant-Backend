using Hangfire.Server;
using Serilog.Context;

namespace MeetingAssistant.Api.Infrastructure.Hangfire;

public class HangfireCorrelationFilter : IServerFilter
{
    public void OnPerforming(PerformingContext filterContext)
    {
        var correlationId = filterContext.GetJobParameter<string>("CorrelationId") ?? Guid.NewGuid().ToString();
        filterContext.SetJobParameter("CorrelationId", correlationId);

        // Push to Serilog
        var logContext = LogContext.PushProperty("CorrelationId", correlationId);
        filterContext.Items["LogContext"] = logContext;

        /* Note: Attempting to resolve ICorrelationIdProvider here is tricky directly
           because creating a *new* scope won't share the instance with the job's execution scope. 
           We rely on Serilog's LogContext for logging, which represents 99% of correlation tracking. */
    }

    public void OnPerformed(PerformedContext filterContext)
    {
        if (filterContext.Items.TryGetValue("LogContext", out var logContext) && logContext is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }
}
