using System.Diagnostics;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using TCJ.Core.Diagnostics;
using TCJ.Core.HealthChecks;

namespace TCJ.DependencyInjection.HealthChecks;

internal sealed class TcjStartupHealthCheck(TcjStartupDiagnostics diagnostics) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        long started = Stopwatch.GetTimestamp();
        using Activity? activity = HealthCheckTelemetryDiagnostics.Start(TcjHealthCheckNames.Checks.Startup, TcjHealthCheckNames.Categories.Startup);
        HealthCheckResult result = diagnostics.HasErrors
            ? HealthCheckResult.Unhealthy("TCJ startup diagnostics contain configuration errors.")
            : diagnostics.HasWarnings
                ? HealthCheckResult.Degraded("TCJ startup diagnostics contain non-fatal warnings.")
                : HealthCheckResult.Healthy("TCJ startup diagnostics are clear.");
        HealthCheckTelemetryDiagnostics.Complete(activity, TcjHealthCheckNames.Checks.Startup, TcjHealthCheckNames.Categories.Startup, ToStatus(result.Status), Stopwatch.GetElapsedTime(started));
        return Task.FromResult(result);
    }

    private static string ToStatus(HealthStatus status) => status switch
    {
        HealthStatus.Healthy => "healthy",
        HealthStatus.Degraded => "degraded",
        _ => "unhealthy"
    };
}
