using System.Diagnostics;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using TCJ.Core.Diagnostics;
using TCJ.Core.HealthChecks;

namespace TCJ.DependencyInjection.HealthChecks;

internal sealed class TcjCoreHealthCheck(TcjStartupDiagnostics diagnostics) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        long started = Stopwatch.GetTimestamp();
        using Activity? activity = HealthCheckTelemetryDiagnostics.Start(TcjHealthCheckNames.Checks.Core, TcjHealthCheckNames.Categories.Liveness);
        HealthCheckResult result;
        try
        {
            if (string.IsNullOrWhiteSpace(TcjTelemetry.FrameworkVersion))
            {
                result = HealthCheckResult.Unhealthy("TCJ framework version metadata is unavailable.");
            }
            else if (diagnostics.HasFatalErrors)
            {
                result = HealthCheckResult.Unhealthy("A fatal TCJ startup diagnostic was recorded.");
            }
            else
            {
                result = HealthCheckResult.Healthy("TCJ in-process services are alive.");
            }

            HealthCheckTelemetryDiagnostics.Complete(activity, TcjHealthCheckNames.Checks.Core, TcjHealthCheckNames.Categories.Liveness, ToStatus(result.Status), Stopwatch.GetElapsedTime(started));
            return Task.FromResult(result);
        }
        catch (OperationCanceledException)
        {
            HealthCheckTelemetryDiagnostics.CompleteCanceled(activity, TcjHealthCheckNames.Checks.Core, TcjHealthCheckNames.Categories.Liveness, Stopwatch.GetElapsedTime(started));
            throw;
        }
    }

    private static string ToStatus(HealthStatus status) => status switch
    {
        HealthStatus.Healthy => "healthy",
        HealthStatus.Degraded => "degraded",
        _ => "unhealthy"
    };
}
