using System.Diagnostics;
using System.Diagnostics.Metrics;
using TCJ.Messaging.Envelopes;

namespace TCJ.Messaging.RabbitMQ.Diagnostics;

internal static class RabbitMqDiagnostics
{
    internal static readonly ActivitySource ActivitySource = new(TcjRabbitMqDiagnosticNames.ActivitySourceName);
    internal static readonly Meter Meter = new(TcjRabbitMqDiagnosticNames.MeterName);
    private static readonly UpDownCounter<long> Connections = Meter.CreateUpDownCounter<long>(TcjRabbitMqDiagnosticNames.Metrics.ConnectionsOpen);
    private static readonly UpDownCounter<long> Channels = Meter.CreateUpDownCounter<long>(TcjRabbitMqDiagnosticNames.Metrics.ChannelsOpen);
    private static readonly Counter<long> Published = Meter.CreateCounter<long>(TcjRabbitMqDiagnosticNames.Metrics.MessagesPublished);
    private static readonly Counter<long> Confirmed = Meter.CreateCounter<long>(TcjRabbitMqDiagnosticNames.Metrics.MessagesConfirmed);
    private static readonly Counter<long> Nacked = Meter.CreateCounter<long>(TcjRabbitMqDiagnosticNames.Metrics.MessagesNacked);
    private static readonly Counter<long> Returned = Meter.CreateCounter<long>(TcjRabbitMqDiagnosticNames.Metrics.MessagesReturned);
    private static readonly Counter<long> Received = Meter.CreateCounter<long>(TcjRabbitMqDiagnosticNames.Metrics.MessagesReceived);
    private static readonly Counter<long> Acked = Meter.CreateCounter<long>(TcjRabbitMqDiagnosticNames.Metrics.MessagesAcked);
    private static readonly Counter<long> Requeued = Meter.CreateCounter<long>(TcjRabbitMqDiagnosticNames.Metrics.MessagesRequeued);
    private static readonly Counter<long> DeadLettered = Meter.CreateCounter<long>(TcjRabbitMqDiagnosticNames.Metrics.MessagesDeadLettered);
    private static readonly Counter<long> Reconnects = Meter.CreateCounter<long>(TcjRabbitMqDiagnosticNames.Metrics.Reconnects);
    private static readonly Histogram<double> PublishDuration = Meter.CreateHistogram<double>(TcjRabbitMqDiagnosticNames.Metrics.PublishDuration, "ms");
    private static readonly Histogram<double> ProcessingDuration = Meter.CreateHistogram<double>(TcjRabbitMqDiagnosticNames.Metrics.ProcessingDuration, "ms");

    internal static Activity? Start(string name, string operation, string? exchange = null, string? queue = null, string? routingKey = null,
        TransportMessageEnvelope? message = null, ActivityKind kind = ActivityKind.Client, ActivityContext parentContext = default)
    {
        Activity? activity = parentContext != default
            ? ActivitySource.StartActivity(name, kind, parentContext)
            : ActivitySource.StartActivity(name, kind);
        if (activity is null) return null;
        activity.SetTag(TcjRabbitMqDiagnosticNames.Tags.MessagingSystem, "rabbitmq");
        activity.SetTag(TcjRabbitMqDiagnosticNames.Tags.Operation, operation);
        if (exchange is not null) activity.SetTag(TcjRabbitMqDiagnosticNames.Tags.Exchange, Bound(exchange));
        if (queue is not null) activity.SetTag(TcjRabbitMqDiagnosticNames.Tags.Queue, Bound(queue));
        if (routingKey is not null) activity.SetTag(TcjRabbitMqDiagnosticNames.Tags.RoutingKey, Bound(routingKey));
        if (message is not null)
        {
            activity.SetTag(TcjRabbitMqDiagnosticNames.Tags.MessageType, message.MessageType);
            activity.SetTag(TcjRabbitMqDiagnosticNames.Tags.MessageVersion, message.MessageVersion);
        }
        return activity;
    }

    internal static void ConnectionOpened() => Connections.Add(1, new(TcjRabbitMqDiagnosticNames.Tags.MessagingSystem, "rabbitmq"));
    internal static void ConnectionClosed() => Connections.Add(-1, new(TcjRabbitMqDiagnosticNames.Tags.MessagingSystem, "rabbitmq"));
    internal static void ChannelOpened() => Channels.Add(1, new(TcjRabbitMqDiagnosticNames.Tags.MessagingSystem, "rabbitmq"));
    internal static void ChannelClosed() => Channels.Add(-1, new(TcjRabbitMqDiagnosticNames.Tags.MessagingSystem, "rabbitmq"));
    internal static void Recovered() => Reconnects.Add(1, new(TcjRabbitMqDiagnosticNames.Tags.MessagingSystem, "rabbitmq"));
    internal static void PublishStarted() => Published.Add(1, new(TcjRabbitMqDiagnosticNames.Tags.MessagingSystem, "rabbitmq"));
    internal static void PublishConfirmed() => Confirmed.Add(1, new(TcjRabbitMqDiagnosticNames.Tags.MessagingSystem, "rabbitmq"));
    internal static void PublishNacked() => Nacked.Add(1, new(TcjRabbitMqDiagnosticNames.Tags.MessagingSystem, "rabbitmq"));
    internal static void PublishReturned() => Returned.Add(1, new(TcjRabbitMqDiagnosticNames.Tags.MessagingSystem, "rabbitmq"));
    internal static void MessageReceived() => Received.Add(1, new(TcjRabbitMqDiagnosticNames.Tags.MessagingSystem, "rabbitmq"));
    internal static void MessageAcked() => Acked.Add(1, new(TcjRabbitMqDiagnosticNames.Tags.MessagingSystem, "rabbitmq"));
    internal static void MessageRequeued() => Requeued.Add(1, new(TcjRabbitMqDiagnosticNames.Tags.MessagingSystem, "rabbitmq"));
    internal static void MessageDeadLettered() => DeadLettered.Add(1, new(TcjRabbitMqDiagnosticNames.Tags.MessagingSystem, "rabbitmq"));
    internal static void RecordPublishDuration(double milliseconds) => PublishDuration.Record(milliseconds, new(TcjRabbitMqDiagnosticNames.Tags.MessagingSystem, "rabbitmq"));
    internal static void RecordProcessingDuration(double milliseconds) => ProcessingDuration.Record(milliseconds, new(TcjRabbitMqDiagnosticNames.Tags.MessagingSystem, "rabbitmq"));

    internal static ActivityContext ExtractParent(TransportMessageEnvelope message)
    {
        if (!message.Headers.TryGetValue("traceparent", out string? traceParent)) return default;
        message.Headers.TryGetValue("tracestate", out string? traceState);
        return ActivityContext.TryParse(traceParent, traceState, isRemote: true, out ActivityContext parsed) ? parsed : default;
    }

    private static string Bound(string value)
    {
        string safe = new(value.Where(static c => !char.IsControl(c)).Take(128).ToArray());
        return safe;
    }
}
