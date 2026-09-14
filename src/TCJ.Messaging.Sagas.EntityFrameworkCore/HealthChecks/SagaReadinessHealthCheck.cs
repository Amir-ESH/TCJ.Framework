using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using TCJ.Messaging.Sagas.EntityFrameworkCore.Processing;

namespace TCJ.Messaging.Sagas.EntityFrameworkCore.HealthChecks;

internal sealed class SagaReadinessHealthCheck(IServiceScopeFactory scopeFactory) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            using IServiceScope scope = scopeFactory.CreateScope();
            await scope.ServiceProvider.GetRequiredService<ISagaStartupValidator>().ValidateAsync(cancellationToken).ConfigureAwait(false);
            return HealthCheckResult.Healthy("Saga infrastructure is ready.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return HealthCheckResult.Unhealthy("Saga infrastructure is not ready.", data: new Dictionary<string, object>
            {
                ["failureType"] = exception.GetType().Name
            });
        }
    }
}
