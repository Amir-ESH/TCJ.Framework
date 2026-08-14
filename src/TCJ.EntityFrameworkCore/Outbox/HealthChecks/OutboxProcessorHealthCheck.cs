using Microsoft.Extensions.Diagnostics.HealthChecks;
using TCJ.Core.Outbox;

namespace TCJ.EntityFrameworkCore.Outbox.HealthChecks;

internal sealed class OutboxProcessorHealthCheck(OutboxProcessorState state) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!state.IsStarted)
        {
            return Task.FromResult(HealthCheckResult.Degraded("The outbox processor has not completed its first poll yet."));
        }

        if (!string.IsNullOrWhiteSpace(state.LastFailureType))
        {
            return Task.FromResult(HealthCheckResult.Unhealthy("The most recent outbox processor poll failed. Inspect sanitized application diagnostics for the failure category."));
        }

        return Task.FromResult(HealthCheckResult.Healthy("The outbox processor is running."));
    }
}
