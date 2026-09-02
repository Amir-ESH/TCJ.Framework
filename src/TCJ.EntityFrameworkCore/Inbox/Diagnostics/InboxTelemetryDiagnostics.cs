using System.Diagnostics;
using System.Diagnostics.Metrics;
using TCJ.Core.Diagnostics;
using TCJ.Core.Inbox;

namespace TCJ.EntityFrameworkCore.Inbox.Diagnostics;

internal static class InboxTelemetryDiagnostics
{
    internal static readonly ActivitySource ActivitySource = new(TcjDiagnosticNames.Sources.EntityFrameworkCore);
    internal static readonly Meter Meter = new(TcjDiagnosticNames.Sources.EntityFrameworkCore);
    private static readonly Counter<long> Received = Meter.CreateCounter<long>(TcjDiagnosticNames.Metrics.InboxMessagesReceived, unit: "{message}");
    private static readonly Counter<long> Processed = Meter.CreateCounter<long>(TcjDiagnosticNames.Metrics.InboxMessagesProcessed, unit: "{message}");
    private static readonly Counter<long> Duplicates = Meter.CreateCounter<long>(TcjDiagnosticNames.Metrics.InboxMessagesDuplicates, unit: "{message}");
    private static readonly Counter<long> Failed = Meter.CreateCounter<long>(TcjDiagnosticNames.Metrics.InboxMessagesFailed, unit: "{message}");
    private static readonly Counter<long> Retried = Meter.CreateCounter<long>(TcjDiagnosticNames.Metrics.InboxMessagesRetried, unit: "{message}");
    private static readonly Counter<long> DeadLettered = Meter.CreateCounter<long>(TcjDiagnosticNames.Metrics.InboxMessagesDeadLettered, unit: "{message}");
    private static readonly Histogram<double> Duration = Meter.CreateHistogram<double>(TcjDiagnosticNames.Metrics.InboxProcessingDuration, unit: "ms");
    private static readonly Histogram<long> Pending = Meter.CreateHistogram<long>(TcjDiagnosticNames.Metrics.InboxPendingCount, unit: "{message}");
    private static readonly Histogram<double> Oldest = Meter.CreateHistogram<double>(TcjDiagnosticNames.Metrics.InboxOldestPendingAge, unit: "s");

    internal static Activity? Start(
        string activityName,
        string operationName,
        string consumer,
        string messageType,
        int version,
        int? attempt = null,
        string? provider = null,
        IReadOnlyDictionary<string, string>? headers = null,
        ActivityKind kind = ActivityKind.Consumer)
    {
        if (!TcjTelemetry.GetOptions().EnableTracing) return null;
        Activity? activity;
        if (TryGetRemoteParent(headers, out ActivityContext remoteParent))
        {
            activity = ActivitySource.StartActivity(activityName, kind, remoteParent);
        }
        else
        {
            activity = ActivitySource.StartActivity(activityName, kind);
        }
        if (activity is null) return null;
        activity.SetTag(TcjDiagnosticNames.Tags.OperationName, operationName);
        activity.SetTag(TcjDiagnosticNames.Tags.InboxConsumer, consumer);
        activity.SetTag(TcjDiagnosticNames.Tags.InboxMessageType, messageType);
        activity.SetTag(TcjDiagnosticNames.Tags.InboxMessageVersion, version);
        if (attempt.HasValue) activity.SetTag(TcjDiagnosticNames.Tags.InboxAttempt, attempt.Value);
        if (!string.IsNullOrWhiteSpace(provider)) activity.SetTag(TcjDiagnosticNames.Tags.InboxProvider, provider);
        return activity;
    }

    internal static void Complete(Activity? activity, InboxHandlingOutcome outcome, InboxFailureType? failureType = null)
    {
        if (activity is null) return;
        string value = outcome.ToString().ToLowerInvariant();
        activity.SetTag(TcjDiagnosticNames.Tags.InboxOutcome, value);
        activity.SetTag(TcjDiagnosticNames.Tags.OperationOutcome, value);
        if (failureType.HasValue) activity.SetTag(TcjDiagnosticNames.Tags.InboxFailureType, failureType.Value.ToString());
        activity.SetStatus(outcome is InboxHandlingOutcome.Acknowledge or InboxHandlingOutcome.IgnoreDuplicate ? ActivityStatusCode.Ok : ActivityStatusCode.Error);
    }

    internal static void RecordReceived(string consumer, string messageType, int version)
    {
        if (!TcjTelemetry.GetOptions().EnableMetrics) return;
        TagList tags = Tags(consumer, messageType, version); Received.Add(1, tags);
    }
    internal static void RecordDuplicate(string consumer, string messageType, int version)
    {
        if (!TcjTelemetry.GetOptions().EnableMetrics) return;
        TagList tags = Tags(consumer, messageType, version); Duplicates.Add(1, tags);
    }
    internal static void RecordProcessed(string consumer, string messageType, int version, double milliseconds)
    {
        if (!TcjTelemetry.GetOptions().EnableMetrics) return;
        TagList tags = Tags(consumer, messageType, version); Processed.Add(1, tags); Duration.Record(milliseconds, tags);
    }
    internal static void RecordFailure(string consumer, string messageType, int version, InboxFailureType type, bool retry)
    {
        if (!TcjTelemetry.GetOptions().EnableMetrics) return;
        TagList tags = Tags(consumer, messageType, version); tags.Add(TcjDiagnosticNames.Tags.InboxFailureType, type.ToString()); Failed.Add(1, tags); if (retry) Retried.Add(1, tags); else DeadLettered.Add(1, tags);
    }
    internal static void RecordBacklog(InboxHealthSnapshot snapshot)
    {
        if (!TcjTelemetry.GetOptions().EnableMetrics) return;
        Pending.Record(snapshot.PendingCount); Oldest.Record(Math.Max(0, snapshot.OldestPendingAge.TotalSeconds));
    }
    private static TagList Tags(string consumer, string messageType, int version)
    {
        TagList tags = default; tags.Add(TcjDiagnosticNames.Tags.InboxConsumer, consumer); tags.Add(TcjDiagnosticNames.Tags.InboxMessageType, messageType); tags.Add(TcjDiagnosticNames.Tags.InboxMessageVersion, version); return tags;
    }

    private static bool TryGetRemoteParent(IReadOnlyDictionary<string, string>? headers, out ActivityContext context)
    {
        context = default;
        if (headers is null || !headers.TryGetValue("traceparent", out string? traceParent) || string.IsNullOrWhiteSpace(traceParent))
        {
            return false;
        }

        headers.TryGetValue("tracestate", out string? traceState);
        return ActivityContext.TryParse(traceParent, traceState, isRemote: true, out context);
    }
}
