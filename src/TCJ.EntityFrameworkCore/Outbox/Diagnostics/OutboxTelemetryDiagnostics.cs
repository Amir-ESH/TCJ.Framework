using System.Diagnostics;
using System.Diagnostics.Metrics;
using TCJ.Core.Diagnostics;

namespace TCJ.EntityFrameworkCore.Outbox.Diagnostics;

internal static class OutboxTelemetryDiagnostics
{
    internal static readonly ActivitySource ActivitySource = new(TcjDiagnosticNames.Sources.EntityFrameworkCore);
    internal static readonly Meter Meter = new(TcjDiagnosticNames.Sources.EntityFrameworkCore);

    private static readonly Counter<long> Persisted = Meter.CreateCounter<long>(TcjDiagnosticNames.Metrics.OutboxMessagesPersisted, unit: "{message}");
    private static readonly Counter<long> Processed = Meter.CreateCounter<long>(TcjDiagnosticNames.Metrics.OutboxMessagesProcessed, unit: "{message}");
    private static readonly Counter<long> Failed = Meter.CreateCounter<long>(TcjDiagnosticNames.Metrics.OutboxMessagesFailed, unit: "{message}");
    private static readonly Counter<long> Retried = Meter.CreateCounter<long>(TcjDiagnosticNames.Metrics.OutboxMessagesRetried, unit: "{message}");
    private static readonly Counter<long> DeadLettered = Meter.CreateCounter<long>(TcjDiagnosticNames.Metrics.OutboxMessagesDeadLettered, unit: "{message}");
    private static readonly Histogram<double> ProcessingDuration = Meter.CreateHistogram<double>(TcjDiagnosticNames.Metrics.OutboxProcessingDuration, unit: "ms");
    private static readonly Histogram<long> PendingCount = Meter.CreateHistogram<long>(TcjDiagnosticNames.Metrics.OutboxPendingCount, unit: "{message}");
    private static readonly Histogram<double> OldestPendingAge = Meter.CreateHistogram<double>(TcjDiagnosticNames.Metrics.OutboxOldestPendingAge, unit: "s");
    private static readonly string PackageVersion = typeof(OutboxTelemetryDiagnostics).Assembly.GetName().Version?.ToString() ?? "unknown";

    internal static Activity? Start(string activityName, string operationName, string? eventType = null, int? attempt = null, string? provider = null)
    {
        if (!TcjTelemetry.GetOptions().EnableTracing)
        {
            return null;
        }

        Activity? activity = ActivitySource.StartActivity(activityName, ActivityKind.Internal);
        if (activity is null)
        {
            return null;
        }

        activity.SetTag(TcjDiagnosticNames.Tags.PackageName, TcjDiagnosticNames.Sources.EntityFrameworkCore);
        activity.SetTag(TcjDiagnosticNames.Tags.PackageVersion, PackageVersion);
        activity.SetTag(TcjDiagnosticNames.Tags.OperationName, operationName);
        if (!string.IsNullOrWhiteSpace(eventType))
        {
            activity.SetTag(TcjDiagnosticNames.Tags.OutboxEventType, eventType);
        }
        if (attempt.HasValue)
        {
            activity.SetTag(TcjDiagnosticNames.Tags.OutboxAttempt, attempt.Value);
        }
        if (!string.IsNullOrWhiteSpace(provider))
        {
            activity.SetTag(TcjDiagnosticNames.Tags.OutboxProvider, provider);
        }
        return activity;
    }

    internal static void CompleteSuccess(Activity? activity)
    {
        if (activity is null)
        {
            return;
        }
        activity.SetTag(TcjDiagnosticNames.Tags.OutboxOutcome, TcjDiagnosticNames.Outcomes.Success);
        activity.SetTag(TcjDiagnosticNames.Tags.OperationOutcome, TcjDiagnosticNames.Outcomes.Success);
        activity.SetStatus(ActivityStatusCode.Ok);
    }

    internal static void CompleteCanceled(Activity? activity)
    {
        if (activity is null)
        {
            return;
        }
        activity.SetTag(TcjDiagnosticNames.Tags.OutboxOutcome, TcjDiagnosticNames.Outcomes.Canceled);
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
        activity.SetTag(TcjDiagnosticNames.Tags.OutboxOutcome, TcjDiagnosticNames.Outcomes.Failure);
        activity.SetTag(TcjDiagnosticNames.Tags.OperationOutcome, TcjDiagnosticNames.Outcomes.Failure);
        activity.SetTag(TcjDiagnosticNames.Tags.ExceptionType, NormalizeType(exception.GetType()));
        activity.SetStatus(ActivityStatusCode.Error);
    }

    internal static void RecordPersisted(int count)
    {
        if (count > 0 && TcjTelemetry.GetOptions().EnableMetrics)
        {
            Persisted.Add(count);
        }
    }

    internal static void RecordProcessed(string eventType, double durationMilliseconds)
    {
        if (!TcjTelemetry.GetOptions().EnableMetrics)
        {
            return;
        }
        TagList tags = default;
        tags.Add(TcjDiagnosticNames.Tags.OutboxEventType, eventType);
        Processed.Add(1, tags);
        ProcessingDuration.Record(durationMilliseconds, tags);
    }

    internal static void RecordFailure(string eventType, int attempt, bool retryScheduled)
    {
        if (!TcjTelemetry.GetOptions().EnableMetrics)
        {
            return;
        }
        TagList tags = default;
        tags.Add(TcjDiagnosticNames.Tags.OutboxEventType, eventType);
        tags.Add(TcjDiagnosticNames.Tags.OutboxAttempt, attempt);
        Failed.Add(1, tags);
        if (retryScheduled)
        {
            Retried.Add(1, tags);
        }
        else
        {
            DeadLettered.Add(1, tags);
        }
    }

    internal static void RecordBacklog(OutboxHealthSnapshot snapshot)
    {
        if (!TcjTelemetry.GetOptions().EnableMetrics)
        {
            return;
        }
        PendingCount.Record(snapshot.PendingCount);
        OldestPendingAge.Record(Math.Max(0d, snapshot.OldestPendingAge.TotalSeconds));
    }

    internal static string NormalizeType(Type type)
    {
        string value = type.FullName ?? type.Name;
        int genericMarker = value.IndexOf('`');
        return (genericMarker >= 0 ? value[..genericMarker] : value).Replace('+', '.');
    }
}
