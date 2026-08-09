using System.Diagnostics;
using System.Reflection;
using System.Threading;

namespace TCJ.Core.Diagnostics;

/// <summary>
/// Configures process-wide TCJ diagnostic behavior without selecting or requiring
/// a telemetry backend, collector, or exporter.
/// </summary>
public static class TcjTelemetry
{
    private static TelemetrySnapshot _current = TelemetrySnapshot.Default;

    /// <summary>
    /// Replaces the current TCJ telemetry configuration with values produced by
    /// <paramref name="configure"/>.
    /// </summary>
    /// <param name="configure">Configuration applied to a fresh options instance.</param>
    public static void Configure(Action<TcjTelemetryOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        var options = new TcjTelemetryOptions();
        configure(options);
        Volatile.Write(ref _current, TelemetrySnapshot.From(options));
    }

    /// <summary>
    /// Returns a detached copy of the current options. Changing the returned object
    /// does not reconfigure framework instrumentation.
    /// </summary>
    /// <returns>A detached snapshot of the current telemetry options.</returns>
    public static TcjTelemetryOptions GetOptions()
    {
        TelemetrySnapshot snapshot = Volatile.Read(ref _current);
        return snapshot.ToOptions();
    }

    /// <summary>
    /// Gets the TCJ.Core package version used to enrich framework-level telemetry resources.
    /// </summary>
    public static string FrameworkVersion => CoreTelemetryDiagnostics.PackageVersion;

    internal static bool TracingEnabled => Volatile.Read(ref _current).EnableTracing;

    internal static bool MetricsEnabled => Volatile.Read(ref _current).EnableMetrics;

    internal static bool RecordExceptionMessages => Volatile.Read(ref _current).RecordExceptionMessages;

    internal static bool RecordEntityTypeNames => Volatile.Read(ref _current).RecordEntityTypeNames;

    internal static bool RecordHandlerTypeNames => Volatile.Read(ref _current).RecordHandlerTypeNames;

    internal static Activity? StartActivity(
        ActivitySource source,
        string activityName,
        string packageName,
        string packageVersion,
        string operationName)
    {
        if (!TracingEnabled)
        {
            return null;
        }

        Activity? activity = source.StartActivity(activityName, ActivityKind.Internal);
        if (activity is null)
        {
            return null;
        }

        activity.SetTag(TcjDiagnosticNames.Tags.PackageName, packageName);
        activity.SetTag(TcjDiagnosticNames.Tags.PackageVersion, packageVersion);
        activity.SetTag(TcjDiagnosticNames.Tags.OperationName, operationName);
        return activity;
    }

    internal static void CompleteSuccess(Activity? activity)
    {
        if (activity is null)
        {
            return;
        }

        activity.SetTag(TcjDiagnosticNames.Tags.OperationOutcome, TcjDiagnosticNames.Outcomes.Success);
        activity.SetStatus(ActivityStatusCode.Ok);
    }

    internal static void CompleteCanceled(Activity? activity)
    {
        if (activity is null)
        {
            return;
        }

        activity.SetTag(TcjDiagnosticNames.Tags.OperationOutcome, TcjDiagnosticNames.Outcomes.Canceled);
        activity.SetTag(TcjDiagnosticNames.Tags.Canceled, true);
        activity.SetStatus(ActivityStatusCode.Unset);
    }

    internal static void CompleteFailure(Activity? activity, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        if (activity is null)
        {
            return;
        }

        activity.SetTag(TcjDiagnosticNames.Tags.OperationOutcome, TcjDiagnosticNames.Outcomes.Failure);
        activity.SetTag(TcjDiagnosticNames.Tags.ExceptionType, NormalizeTypeName(exception.GetType()));
        if (RecordExceptionMessages)
        {
            activity.SetTag(TcjDiagnosticNames.Tags.ExceptionMessage, exception.Message);
        }

        activity.SetStatus(ActivityStatusCode.Error);
    }

    internal static string NormalizeTypeName(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);

        string value = type.FullName ?? type.Name;
        int genericMarker = value.IndexOf('`');
        if (genericMarker >= 0)
        {
            value = value[..genericMarker];
        }

        return value.Replace('+', '.');
    }

    internal static void ResetForTests() => Volatile.Write(ref _current, TelemetrySnapshot.Default);

    private sealed record TelemetrySnapshot(
        bool EnableTracing,
        bool EnableMetrics,
        bool RecordExceptionMessages,
        bool RecordEntityTypeNames,
        bool RecordHandlerTypeNames)
    {
        internal static TelemetrySnapshot Default { get; } = From(new TcjTelemetryOptions());

        internal static TelemetrySnapshot From(TcjTelemetryOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);
            return new(
                options.EnableTracing,
                options.EnableMetrics,
                options.RecordExceptionMessages,
                options.RecordEntityTypeNames,
                options.RecordHandlerTypeNames);
        }

        internal TcjTelemetryOptions ToOptions() => new()
        {
            EnableTracing = EnableTracing,
            EnableMetrics = EnableMetrics,
            RecordExceptionMessages = RecordExceptionMessages,
            RecordEntityTypeNames = RecordEntityTypeNames,
            RecordHandlerTypeNames = RecordHandlerTypeNames
        };
    }
}

internal static class TcjPackageMetadata
{
    internal static string GetPackageVersion(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        string? packageVersion = assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(static attribute =>
                string.Equals(attribute.Key, "PackageVersion", StringComparison.Ordinal))
            ?.Value;

        if (!string.IsNullOrWhiteSpace(packageVersion))
        {
            return packageVersion;
        }

        string? informationalVersion = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;

        if (!string.IsNullOrWhiteSpace(informationalVersion))
        {
            int metadataSeparator = informationalVersion.IndexOf('+');
            return metadataSeparator >= 0
                ? informationalVersion[..metadataSeparator]
                : informationalVersion;
        }

        return assembly.GetName().Version?.ToString() ?? "unknown";
    }
}
