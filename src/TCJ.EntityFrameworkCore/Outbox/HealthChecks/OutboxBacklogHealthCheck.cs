using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using TCJ.Core.Outbox;
using TCJ.EntityFrameworkCore.Outbox.Diagnostics;

namespace TCJ.EntityFrameworkCore.Outbox.HealthChecks;

internal sealed class OutboxBacklogHealthCheck(
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
            OutboxTelemetryDiagnostics.RecordBacklog(snapshot);
            return snapshot.OldestPendingAge > options.BacklogUnhealthyAge
                ? HealthCheckResult.Unhealthy("The oldest eligible outbox message exceeds the configured readiness threshold.")
                : HealthCheckResult.Healthy("The outbox backlog is within the configured readiness threshold.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return HealthCheckResult.Unhealthy("Outbox backlog health could not be evaluated safely.");
        }
    }
}
