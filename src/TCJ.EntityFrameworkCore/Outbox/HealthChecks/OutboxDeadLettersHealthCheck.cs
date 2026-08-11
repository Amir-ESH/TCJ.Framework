using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using TCJ.Core.Outbox;

namespace TCJ.EntityFrameworkCore.Outbox.HealthChecks;

internal sealed class OutboxDeadLettersHealthCheck(
    IServiceScopeFactory scopeFactory,
    TcjOutboxOptions options,
    TimeProvider timeProvider) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
        IOutboxStorage? storage = scope.ServiceProvider.GetService<IOutboxStorage>();
        if (storage is null)
        {
            return HealthCheckResult.Unhealthy("No provider-specific outbox storage is registered.");
        }

        try
        {
            OutboxHealthSnapshot snapshot = await storage.GetHealthSnapshotAsync(timeProvider.GetUtcNow(), cancellationToken).ConfigureAwait(false);
            if (options.DeadLetterUnhealthyThreshold == 0 || snapshot.DeadLetterCount < options.DeadLetterUnhealthyThreshold)
            {
                return HealthCheckResult.Healthy("Outbox dead-letter volume is within the configured threshold.");
            }

            return HealthCheckResult.Degraded("Outbox dead-letter volume reached the configured readiness threshold.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return HealthCheckResult.Unhealthy("Outbox dead-letter health could not be evaluated safely.");
        }
    }
}
