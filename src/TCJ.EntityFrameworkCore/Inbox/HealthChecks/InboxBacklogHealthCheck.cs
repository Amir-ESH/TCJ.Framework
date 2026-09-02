using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using TCJ.Core.Inbox;
using TCJ.EntityFrameworkCore.Inbox.Diagnostics;
using TCJ.EntityFrameworkCore.Inbox.Storage;

namespace TCJ.EntityFrameworkCore.Inbox.HealthChecks;

internal sealed class InboxBacklogHealthCheck(IServiceScopeFactory scopeFactory, TcjInboxOptions options, TimeProvider timeProvider) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
        InboxHealthSnapshot snapshot = await scope.ServiceProvider.GetRequiredService<IInboxStorage>().GetHealthSnapshotAsync(timeProvider.GetUtcNow(), cancellationToken).ConfigureAwait(false);
        InboxTelemetryDiagnostics.RecordBacklog(snapshot);
        var data = new Dictionary<string, object> { ["pendingCount"] = snapshot.PendingCount, ["oldestPendingAgeSeconds"] = Math.Max(0, snapshot.OldestPendingAge.TotalSeconds) };
        return snapshot.OldestPendingAge > options.BacklogUnhealthyAge
            ? HealthCheckResult.Unhealthy("Transactional Inbox backlog exceeds the configured age threshold.", data: data)
            : HealthCheckResult.Healthy("Transactional Inbox backlog is within the configured threshold.", data);
    }
}
