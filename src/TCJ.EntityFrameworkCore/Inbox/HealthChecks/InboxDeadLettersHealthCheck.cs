using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using TCJ.Core.Inbox;
using TCJ.EntityFrameworkCore.Inbox.Storage;

namespace TCJ.EntityFrameworkCore.Inbox.HealthChecks;

internal sealed class InboxDeadLettersHealthCheck(IServiceScopeFactory scopeFactory, TcjInboxOptions options, TimeProvider timeProvider) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
        InboxHealthSnapshot snapshot = await scope.ServiceProvider.GetRequiredService<IInboxStorage>().GetHealthSnapshotAsync(timeProvider.GetUtcNow(), cancellationToken).ConfigureAwait(false);
        var data = new Dictionary<string, object> { ["deadLetterCount"] = snapshot.DeadLetterCount };
        return options.DeadLetterUnhealthyThreshold > 0 && snapshot.DeadLetterCount >= options.DeadLetterUnhealthyThreshold
            ? HealthCheckResult.Degraded("Transactional Inbox contains dead-lettered messages.", data: data)
            : HealthCheckResult.Healthy("Transactional Inbox dead-letter count is within the configured threshold.", data);
    }
}
