using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using TCJ.Core.Diagnostics;
using TCJ.Core.DomainEvents;
using TCJ.Core.HealthChecks;

namespace TCJ.DependencyInjection.HealthChecks;

internal sealed class TcjDomainEventsHealthCheck(IServiceScopeFactory scopeFactory, TcjStartupDiagnostics diagnostics) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        long started = Stopwatch.GetTimestamp();
        using Activity? activity = HealthCheckTelemetryDiagnostics.Start(TcjHealthCheckNames.Checks.DomainEvents, TcjHealthCheckNames.Categories.Dependency);
        try
        {
            using IServiceScope scope = scopeFactory.CreateScope();
            HealthCheckResult result;
            if (scope.ServiceProvider.GetService<IDomainEventDispatcher>() is null)
            {
                diagnostics.Report("TCJ.DomainEvents.MissingDispatcher", "TCJ.DependencyInjection requires IDomainEventDispatcher to be registered before domain-event readiness can succeed.");
                result = HealthCheckResult.Unhealthy("TCJ domain-event dispatcher infrastructure is unavailable.");
            }
            else
            {
                diagnostics.Clear("TCJ.DomainEvents.MissingDispatcher");
                result = HealthCheckResult.Healthy("TCJ domain-event dispatcher infrastructure is available.");
            }

            HealthCheckTelemetryDiagnostics.Complete(activity, TcjHealthCheckNames.Checks.DomainEvents, TcjHealthCheckNames.Categories.Dependency, result.Status == HealthStatus.Healthy ? "healthy" : "unhealthy", Stopwatch.GetElapsedTime(started));
            return Task.FromResult(result);
        }
        catch (OperationCanceledException)
        {
            HealthCheckTelemetryDiagnostics.CompleteCanceled(activity, TcjHealthCheckNames.Checks.DomainEvents, TcjHealthCheckNames.Categories.Dependency, Stopwatch.GetElapsedTime(started));
            throw;
        }
        catch
        {
            diagnostics.Report("TCJ.DomainEvents.ResolutionFailure", "TCJ domain-event dispatcher infrastructure could not be resolved.");
            HealthCheckTelemetryDiagnostics.Complete(activity, TcjHealthCheckNames.Checks.DomainEvents, TcjHealthCheckNames.Categories.Dependency, "unhealthy", Stopwatch.GetElapsedTime(started));
            return Task.FromResult(HealthCheckResult.Unhealthy("TCJ domain-event dispatcher validation failed."));
        }
    }
}
