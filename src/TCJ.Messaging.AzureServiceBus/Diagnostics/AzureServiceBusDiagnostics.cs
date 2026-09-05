using System.Diagnostics;
using System.Diagnostics.Metrics;
using TCJ.Messaging.Envelopes;

namespace TCJ.Messaging.AzureServiceBus.Diagnostics;

internal static class AzureServiceBusDiagnostics
{
    internal static readonly ActivitySource ActivitySource = new(TcjAzureServiceBusDiagnosticNames.ActivitySourceName);
    internal static readonly Meter Meter = new(TcjAzureServiceBusDiagnosticNames.MeterName);
    private static readonly Counter<long> Published = Meter.CreateCounter<long>(TcjAzureServiceBusDiagnosticNames.Metrics.MessagesPublished);
    private static readonly Counter<long> Scheduled = Meter.CreateCounter<long>(TcjAzureServiceBusDiagnosticNames.Metrics.MessagesScheduled);
    private static readonly Counter<long> Received = Meter.CreateCounter<long>(TcjAzureServiceBusDiagnosticNames.Metrics.MessagesReceived);
    private static readonly Counter<long> Completed = Meter.CreateCounter<long>(TcjAzureServiceBusDiagnosticNames.Metrics.MessagesCompleted);
    private static readonly Counter<long> Abandoned = Meter.CreateCounter<long>(TcjAzureServiceBusDiagnosticNames.Metrics.MessagesAbandoned);
    private static readonly Counter<long> Deferred = Meter.CreateCounter<long>(TcjAzureServiceBusDiagnosticNames.Metrics.MessagesDeferred);
    private static readonly Counter<long> DeadLettered = Meter.CreateCounter<long>(TcjAzureServiceBusDiagnosticNames.Metrics.MessagesDeadLettered);
    private static readonly Histogram<double> PublishDuration = Meter.CreateHistogram<double>(TcjAzureServiceBusDiagnosticNames.Metrics.PublishDuration, "ms");
    private static readonly Histogram<double> ProcessingDuration = Meter.CreateHistogram<double>(TcjAzureServiceBusDiagnosticNames.Metrics.ProcessingDuration, "ms");
    private static readonly Counter<long> Renewals = Meter.CreateCounter<long>(TcjAzureServiceBusDiagnosticNames.Metrics.LockRenewals);
    private static readonly Counter<long> LockLosses = Meter.CreateCounter<long>(TcjAzureServiceBusDiagnosticNames.Metrics.LockLosses);
    private static readonly Counter<long> ProcessorErrors = Meter.CreateCounter<long>(TcjAzureServiceBusDiagnosticNames.Metrics.ProcessorErrors);
    private static readonly UpDownCounter<long> ActiveSessions = Meter.CreateUpDownCounter<long>(TcjAzureServiceBusDiagnosticNames.Metrics.ActiveSessions);

    internal static Activity? Start(string activityName, string operation, string? destination = null, string? entityType = null,
        bool? sessionEnabled = null, string? settlement = null, TransportMessageEnvelope? message = null,
        ActivityKind kind = ActivityKind.Client, ActivityContext parentContext = default)
    {
        Activity? activity = parentContext != default
            ? ActivitySource.StartActivity(activityName, kind, parentContext)
            : ActivitySource.StartActivity(activityName, kind);
        if (activity is null) return null;
        activity.SetTag(TcjAzureServiceBusDiagnosticNames.Tags.MessagingSystem, "servicebus");
        activity.SetTag(TcjAzureServiceBusDiagnosticNames.Tags.Operation, operation);
        if (destination is not null) activity.SetTag(TcjAzureServiceBusDiagnosticNames.Tags.Destination, Bound(destination));
        if (entityType is not null) activity.SetTag(TcjAzureServiceBusDiagnosticNames.Tags.EntityType, entityType);
        if (sessionEnabled is not null) activity.SetTag(TcjAzureServiceBusDiagnosticNames.Tags.SessionEnabled, sessionEnabled.Value);
        if (settlement is not null) activity.SetTag(TcjAzureServiceBusDiagnosticNames.Tags.Settlement, settlement);
        if (message is not null)
        {
            activity.SetTag(TcjAzureServiceBusDiagnosticNames.Tags.MessageType, message.MessageType);
            activity.SetTag(TcjAzureServiceBusDiagnosticNames.Tags.MessageVersion, message.MessageVersion);
        }
        return activity;
    }

    internal static void MessagePublished() => Published.Add(1, MetricTags());
    internal static void MessageScheduled() => Scheduled.Add(1, MetricTags());
    internal static void MessageReceived() => Received.Add(1, MetricTags());
    internal static void MessageCompleted() => Completed.Add(1, MetricTags());
    internal static void MessageAbandoned() => Abandoned.Add(1, MetricTags());
    internal static void MessageDeferred() => Deferred.Add(1, MetricTags());
    internal static void MessageDeadLettered() => DeadLettered.Add(1, MetricTags());
    internal static void LockRenewed() => Renewals.Add(1, MetricTags());
    internal static void LockLost() => LockLosses.Add(1, MetricTags());
    internal static void ProcessorError() => ProcessorErrors.Add(1, MetricTags());
    internal static void SessionAccepted() => ActiveSessions.Add(1, MetricTags());
    internal static void SessionReleased() => ActiveSessions.Add(-1, MetricTags());
    internal static void RecordPublishDuration(double milliseconds) => PublishDuration.Record(milliseconds, MetricTags());
    internal static void RecordProcessingDuration(double milliseconds) => ProcessingDuration.Record(milliseconds, MetricTags());

    internal static ActivityContext ExtractParent(TransportMessageEnvelope message)
    {
        if (!message.Headers.TryGetValue("traceparent", out string? traceParent)) return default;
        message.Headers.TryGetValue("tracestate", out string? traceState);
        return ActivityContext.TryParse(traceParent, traceState, isRemote: true, out ActivityContext context) ? context : default;
    }

    private static TagList MetricTags() => new() { { TcjAzureServiceBusDiagnosticNames.Tags.MessagingSystem, "servicebus" } };
    private static string Bound(string value) => new(value.Where(static c => !char.IsControl(c)).Take(128).ToArray());
}
