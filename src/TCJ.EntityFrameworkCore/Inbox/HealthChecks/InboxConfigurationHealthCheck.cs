using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using TCJ.EntityFrameworkCore.Inbox.Processing;

namespace TCJ.EntityFrameworkCore.Inbox.HealthChecks;

internal sealed class InboxConfigurationHealthCheck(IServiceScopeFactory scopeFactory) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<IInboxStartupValidator>().ValidateAsync(cancellationToken).ConfigureAwait(false);
            return HealthCheckResult.Healthy("Transactional Inbox configuration is valid.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) { return HealthCheckResult.Unhealthy("Transactional Inbox configuration is invalid.", data: new Dictionary<string, object> { ["failureType"] = exception.GetType().Name }); }
    }
}
