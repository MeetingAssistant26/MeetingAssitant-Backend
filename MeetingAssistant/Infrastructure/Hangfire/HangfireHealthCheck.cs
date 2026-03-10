using Hangfire;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace MeetingAssistant.Infrastructure.DependencyInjection
{
    public sealed class HangfireHealthCheck(JobStorage jobStorage) : IHealthCheck
    {
        public Task<HealthCheckResult> CheckHealthAsync(
            HealthCheckContext context,
            CancellationToken cancellationToken = default)
        {
            try
            {
                _ = jobStorage.GetMonitoringApi().Servers();
                return Task.FromResult(HealthCheckResult.Healthy("Hangfire storage is reachable."));
            }
            catch (Exception ex)
            {
                return Task.FromResult(HealthCheckResult.Unhealthy("Hangfire storage is unreachable.", ex));
            }
        }
    }
}
