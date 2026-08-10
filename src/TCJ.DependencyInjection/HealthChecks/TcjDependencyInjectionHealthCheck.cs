using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using TCJ.Core.Diagnostics;
using TCJ.Core.HealthChecks;
using TCJ.Core.Identifiers;

namespace TCJ.DependencyInjection.HealthChecks;

internal sealed class TcjDependencyInjectionHealthCheck(IServiceScopeFactory scopeFactory, TcjStartupDiagnostics diagnostics) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        long started = Stopwatch.GetTimestamp();
        using Activity? activity = HealthCheckTelemetryDiagnostics.Start(TcjHealthCheckNames.Checks.DependencyInjection, TcjHealthCheckNames.Categories.Configuration);
        try
        {
            using IServiceScope scope = scopeFactory.CreateScope();
            if (scope.ServiceProvider.GetService<IGuidGenerator>() is null)
            {
                diagnostics.Report("TCJ.DI.MissingGuidGenerator", "TCJ.DependencyInjection requires IGuidGenerator to be registered.");
                return Task.FromResult(Complete(HealthCheckResult.Unhealthy("A required TCJ dependency-injection registration is missing.")));
            }

            if (scope.ServiceProvider.GetService<TimeProvider>() is null)
            {
                diagnostics.Report("TCJ.DI.MissingTimeProvider", "TCJ.DependencyInjection requires TimeProvider to be registered.");
                return Task.FromResult(Complete(HealthCheckResult.Unhealthy("A required TCJ dependency-injection registration is missing.")));
            }

            diagnostics.Clear("TCJ.DI.MissingGuidGenerator");
            diagnostics.Clear("TCJ.DI.MissingTimeProvider");
            return Task.FromResult(Complete(HealthCheckResult.Healthy("TCJ dependency-injection registrations are available.")));
        }
        catch (OperationCanceledException)
        {
            HealthCheckTelemetryDiagnostics.CompleteCanceled(activity, TcjHealthCheckNames.Checks.DependencyInjection, TcjHealthCheckNames.Categories.Configuration, Stopwatch.GetElapsedTime(started));
            throw;
        }
        catch
        {
            diagnostics.Report("TCJ.DI.ResolutionFailure", "TCJ.DependencyInjection could not resolve required framework registrations.");
            return Task.FromResult(Complete(HealthCheckResult.Unhealthy("TCJ dependency-injection validation failed.")));
        }

        HealthCheckResult Complete(HealthCheckResult result)
        {
            HealthCheckTelemetryDiagnostics.Complete(activity, TcjHealthCheckNames.Checks.DependencyInjection, TcjHealthCheckNames.Categories.Configuration, result.Status == HealthStatus.Healthy ? "healthy" : result.Status == HealthStatus.Degraded ? "degraded" : "unhealthy", Stopwatch.GetElapsedTime(started));
            return result;
        }
    }
}
