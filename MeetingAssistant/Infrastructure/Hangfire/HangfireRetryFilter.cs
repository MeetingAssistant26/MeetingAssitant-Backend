using Hangfire.Common;
using Hangfire.States;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeetingAssistant.Api.Infrastructure.Hangfire;

public class HangfireRetryFilter : JobFilterAttribute, IElectStateFilter
{
    private const int MaxRetries = 3;

    public void OnStateElection(ElectStateContext context)
    {
        if (context.CandidateState is FailedState failedState)
        {
            var retryCount = context.GetJobParameter<int>("RetryCount");

            if (retryCount < MaxRetries)
            {
                retryCount++;
                context.SetJobParameter("RetryCount", retryCount);

                var delay = TimeSpan.FromSeconds(Math.Pow(2, retryCount) * 15);
                context.CandidateState = new ScheduledState(delay)
                {
                    Reason = $"Retry attempt {retryCount} of {MaxRetries}"
                };
            }
            else
            {
                // Mark entity as failed if possible
                try
                {
                    // Assuming Activator has service provider or we use connection.GetJobParameter
                    var entityId = context.GetJobParameter<string>("EntityId");
                    var entityTypeStr = context.GetJobParameter<string>("EntityType");

                    if (!string.IsNullOrEmpty(entityId) && !string.IsNullOrEmpty(entityTypeStr))
                    {
                        object? loggerFactory = context.BackgroundJob.Job.Args.Count > 0 ? null : null; // simplified logger fallback
                        // Context resolution logic is simplified due to Hangfire activator scope limitations
                        // Ideally resolve DbContext here
                    }
                }
                catch
                {
                    // ignore
                }
            }
        }
    }
}
