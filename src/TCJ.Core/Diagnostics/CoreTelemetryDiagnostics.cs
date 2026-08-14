using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace TCJ.Core.Diagnostics;

internal static class CoreTelemetryDiagnostics
{
    internal static readonly string PackageVersion =
        TcjPackageMetadata.GetPackageVersion(typeof(TcjTelemetry).Assembly);

    internal static readonly ActivitySource ActivitySource = new(
        TcjDiagnosticNames.Sources.Core,
        PackageVersion);

    internal static readonly Meter Meter = new(
        TcjDiagnosticNames.Sources.Core,
        PackageVersion);

    internal static readonly Counter<long> DomainEventsDispatched = Meter.CreateCounter<long>(
        TcjDiagnosticNames.Metrics.DomainEventsDispatched,
        unit: "{event}",
        description: "Domain events whose dispatch operation completed, failed, or was canceled.");

    internal static readonly Counter<long> DomainEventHandlersCompleted = Meter.CreateCounter<long>(
        TcjDiagnosticNames.Metrics.DomainEventHandlersCompleted,
        unit: "{event}",
        description: "Domain-event handler invocations completed successfully.");

    internal static readonly Counter<long> DomainEventHandlersFailed = Meter.CreateCounter<long>(
        TcjDiagnosticNames.Metrics.DomainEventHandlersFailed,
        unit: "{event}",
        description: "Domain-event handler invocations that failed with an exception.");

    internal static readonly Histogram<double> DomainEventDispatchDuration = Meter.CreateHistogram<double>(
        TcjDiagnosticNames.Metrics.DomainEventDispatchDuration,
        unit: "s",
        description: "Domain-event dispatch duration in seconds.");

    internal static readonly Histogram<double> DomainEventHandlerDuration = Meter.CreateHistogram<double>(
        TcjDiagnosticNames.Metrics.DomainEventHandlerDuration,
        unit: "s",
        description: "Domain-event handler duration in seconds.");
}
