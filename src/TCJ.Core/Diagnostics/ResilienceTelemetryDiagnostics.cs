using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace TCJ.Core.Diagnostics;

internal static class ResilienceTelemetryDiagnostics
{
    internal static readonly Counter<long> Attempts = CoreTelemetryDiagnostics.Meter.CreateCounter<long>(
        TcjDiagnosticNames.Metrics.ResilienceAttempts,
        unit: "{attempt}",
        description: "Attempts executed by explicit TCJ resilience policies.");

    internal static readonly Counter<long> Retries = CoreTelemetryDiagnostics.Meter.CreateCounter<long>(
        TcjDiagnosticNames.Metrics.ResilienceRetries,
        unit: "{retry}",
        description: "Retries scheduled by explicit TCJ resilience policies.");

    internal static readonly Counter<long> Timeouts = CoreTelemetryDiagnostics.Meter.CreateCounter<long>(
        TcjDiagnosticNames.Metrics.ResilienceTimeouts,
        unit: "{timeout}",
        description: "Operations canceled because an explicit TCJ timeout elapsed.");

    internal static readonly Counter<long> CircuitOpen = CoreTelemetryDiagnostics.Meter.CreateCounter<long>(
        TcjDiagnosticNames.Metrics.ResilienceCircuitOpen,
        unit: "{transition}",
        description: "Circuit-breaker transitions into the open state.");

    internal static readonly Counter<long> Failures = CoreTelemetryDiagnostics.Meter.CreateCounter<long>(
        TcjDiagnosticNames.Metrics.ResilienceFailures,
        unit: "{failure}",
        description: "Terminal logical-operation failures observed by explicit TCJ resilience policies.");

    internal static Activity? Start(string activityName, string strategy)
    {
        string telemetryStrategy = NormalizeStrategy(strategy);
        Activity? activity = TcjTelemetry.StartActivity(
            CoreTelemetryDiagnostics.ActivitySource,
            activityName,
            TcjDiagnosticNames.Sources.Core,
            CoreTelemetryDiagnostics.PackageVersion,
            telemetryStrategy);

        activity?.SetTag(TcjDiagnosticNames.Tags.ResilienceStrategy, telemetryStrategy);
        return activity;
    }

    internal static TagList CreateTags(
        string strategy,
        string outcome,
        int? attempt = null,
        string? failureType = null,
        string? circuitState = null)
    {
        var tags = new TagList
        {
            { TcjDiagnosticNames.Tags.ResilienceStrategy, NormalizeStrategy(strategy) },
            { TcjDiagnosticNames.Tags.ResilienceOutcome, outcome }
        };

        if (attempt is int attemptNumber)
        {
            tags.Add(TcjDiagnosticNames.Tags.ResilienceAttempt, attemptNumber);
        }

        if (failureType is not null)
        {
            tags.Add(TcjDiagnosticNames.Tags.ResilienceFailureType, failureType);
        }

        if (circuitState is not null)
        {
            tags.Add(TcjDiagnosticNames.Tags.ResilienceCircuitState, circuitState);
        }

        return tags;
    }

    internal static void RecordAttempt(string strategy, string outcome, int attempt, string? failureType = null)
    {
        if (!TcjTelemetry.MetricsEnabled || !Attempts.Enabled)
        {
            return;
        }

        Attempts.Add(1, CreateTags(strategy, outcome, attempt, failureType));
    }

    internal static void RecordRetry(string strategy, int attempt, string failureType)
    {
        if (!TcjTelemetry.MetricsEnabled || !Retries.Enabled)
        {
            return;
        }

        Retries.Add(1, CreateTags(strategy, "retry", attempt, failureType));
    }

    internal static void RecordTimeout(string strategy)
    {
        if (!TcjTelemetry.MetricsEnabled || !Timeouts.Enabled)
        {
            return;
        }

        Timeouts.Add(1, CreateTags(strategy, "timeout", failureType: "timeout"));
    }

    internal static void RecordCircuitOpen(string strategy)
    {
        if (!TcjTelemetry.MetricsEnabled || !CircuitOpen.Enabled)
        {
            return;
        }

        CircuitOpen.Add(1, CreateTags(strategy, "open", circuitState: "open"));
    }

    internal static void RecordFailure(string strategy, string failureType)
    {
        if (!TcjTelemetry.MetricsEnabled || !Failures.Enabled)
        {
            return;
        }

        Failures.Add(1, CreateTags(strategy, TcjDiagnosticNames.Outcomes.Failure, failureType: failureType));
    }

    // Metrics and activity attributes intentionally collapse arbitrary consumer labels
    // to a bounded value. Public strategy strings are useful for API readability but
    // must never create an unbounded telemetry dimension.
    private static string NormalizeStrategy(string strategy) => strategy switch
    {
        "operation" => "operation",
        "operation_timeout" => "operation_timeout",
        "circuit_breaker" => "circuit_breaker",
        "domain_event_handler" => "domain_event_handler",
        "sqlserver_transaction" => "sqlserver_transaction",
        _ => "custom"
    };
}
