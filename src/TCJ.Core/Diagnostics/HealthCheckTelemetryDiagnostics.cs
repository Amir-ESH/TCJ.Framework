using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace TCJ.Core.Diagnostics;

internal static class HealthCheckTelemetryDiagnostics
{
    internal static readonly Counter<long> Executed = CoreTelemetryDiagnostics.Meter.CreateCounter<long>(
        TcjDiagnosticNames.Metrics.HealthChecksExecuted, unit: "{check}", description: "Completed TCJ health-check executions.");
    internal static readonly Histogram<double> Duration = CoreTelemetryDiagnostics.Meter.CreateHistogram<double>(
        TcjDiagnosticNames.Metrics.HealthCheckDuration, unit: "ms", description: "TCJ health-check execution duration.");
    internal static readonly Counter<long> Failures = CoreTelemetryDiagnostics.Meter.CreateCounter<long>(
        TcjDiagnosticNames.Metrics.HealthCheckFailures, unit: "{failure}", description: "Unhealthy TCJ health-check executions.");
    internal static readonly Counter<long> Status = CoreTelemetryDiagnostics.Meter.CreateCounter<long>(
        TcjDiagnosticNames.Metrics.HealthCheckStatus, unit: "{check}", description: "TCJ health-check results by bounded status.");

    internal static Activity? Start(string name, string category)
    {
        string normalizedName = NormalizeName(name);
        string normalizedCategory = NormalizeCategory(category);
        Activity? activity = TcjTelemetry.StartActivity(
            CoreTelemetryDiagnostics.ActivitySource,
            TcjDiagnosticNames.Activities.HealthCheckExecute,
            TcjDiagnosticNames.Sources.Core,
            CoreTelemetryDiagnostics.PackageVersion,
            "health_check");
        activity?.SetTag(TcjDiagnosticNames.Tags.HealthCheckName, normalizedName);
        activity?.SetTag(TcjDiagnosticNames.Tags.HealthCheckCategory, normalizedCategory);
        return activity;
    }

    internal static void Complete(Activity? activity, string name, string category, string status, TimeSpan duration)
    {
        string normalizedName = NormalizeName(name);
        string normalizedCategory = NormalizeCategory(category);
        string normalizedStatus = NormalizeStatus(status);
        string outcome = normalizedStatus == "unhealthy" ? TcjDiagnosticNames.Outcomes.Failure : TcjDiagnosticNames.Outcomes.Success;

        activity?.SetTag(TcjDiagnosticNames.Tags.HealthCheckStatus, normalizedStatus);
        if (outcome == TcjDiagnosticNames.Outcomes.Failure)
        {
            activity?.SetTag(TcjDiagnosticNames.Tags.OperationOutcome, outcome);
            activity?.SetStatus(ActivityStatusCode.Error);
        }
        else
        {
            TcjTelemetry.CompleteSuccess(activity);
        }

        if (!TcjTelemetry.MetricsEnabled)
        {
            return;
        }

        TagList tags = CreateTags(normalizedName, normalizedCategory, normalizedStatus, outcome);
        if (Executed.Enabled) Executed.Add(1, tags);
        if (Duration.Enabled) Duration.Record(Math.Max(0, duration.TotalMilliseconds), tags);
        if (Status.Enabled) Status.Add(1, tags);
        if (outcome == TcjDiagnosticNames.Outcomes.Failure && Failures.Enabled) Failures.Add(1, tags);
    }

    internal static void CompleteCanceled(Activity? activity, string name, string category, TimeSpan duration)
    {
        activity?.SetTag(TcjDiagnosticNames.Tags.HealthCheckStatus, "canceled");
        TcjTelemetry.CompleteCanceled(activity);
        if (!TcjTelemetry.MetricsEnabled) return;
        TagList tags = CreateTags(NormalizeName(name), NormalizeCategory(category), "canceled", TcjDiagnosticNames.Outcomes.Canceled);
        if (Executed.Enabled) Executed.Add(1, tags);
        if (Duration.Enabled) Duration.Record(Math.Max(0, duration.TotalMilliseconds), tags);
        if (Status.Enabled) Status.Add(1, tags);
    }

    private static TagList CreateTags(string name, string category, string status, string outcome) => new()
    {
        { TcjDiagnosticNames.Tags.HealthCheckName, name },
        { TcjDiagnosticNames.Tags.HealthCheckCategory, category },
        { TcjDiagnosticNames.Tags.HealthCheckStatus, status },
        { TcjDiagnosticNames.Tags.OperationOutcome, outcome }
    };

    private static string NormalizeName(string value) => value switch
    {
        "tcj.core" => "tcj.core",
        "tcj.startup" => "tcj.startup",
        "tcj.dependency_injection" => "tcj.dependency_injection",
        "tcj.domain_events" => "tcj.domain_events",
        "tcj.entity_framework_core" => "tcj.entity_framework_core",
        "tcj.sqlserver" => "tcj.sqlserver",
        "tcj.sqlserver.migrations" => "tcj.sqlserver.migrations",
        _ => "custom"
    };

    private static string NormalizeCategory(string value) => value switch
    {
        "liveness" => "liveness",
        "readiness" => "readiness",
        "dependency" => "dependency",
        "startup" => "startup",
        "database" => "database",
        "sqlserver" => "sqlserver",
        "configuration" => "configuration",
        _ => "custom"
    };

    private static string NormalizeStatus(string value) => value switch
    {
        "healthy" => "healthy",
        "degraded" => "degraded",
        "unhealthy" => "unhealthy",
        "canceled" => "canceled",
        _ => "unknown"
    };
}
