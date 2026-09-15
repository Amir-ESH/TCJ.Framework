using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace TCJ.Messaging.Sagas.EntityFrameworkCore.HealthChecks;

internal sealed class SagaLivenessHealthCheck : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(HealthCheckResult.Healthy("Saga process is alive."));
    }
}
