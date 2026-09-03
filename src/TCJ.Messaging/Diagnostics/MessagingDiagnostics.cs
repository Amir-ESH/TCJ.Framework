using System.Diagnostics;
using System.Diagnostics.Metrics;
using TCJ.Messaging.Envelopes;
using TCJ.Messaging.Publishing;

namespace TCJ.Messaging.Diagnostics;

internal static class MessagingDiagnostics
{
    private static readonly ActivitySource ActivitySource = new(TcjMessagingDiagnosticNames.Source);
    private static readonly Meter Meter = new(TcjMessagingDiagnosticNames.Source);
    private static readonly Counter<long> Published = Meter.CreateCounter<long>(TcjMessagingDiagnosticNames.Metrics.MessagesPublished);
    private static readonly Counter<long> Received = Meter.CreateCounter<long>(TcjMessagingDiagnosticNames.Metrics.MessagesReceived);
    private static readonly Counter<long> Completed = Meter.CreateCounter<long>(TcjMessagingDiagnosticNames.Metrics.MessagesCompleted);
    private static readonly Counter<long> Retried = Meter.CreateCounter<long>(TcjMessagingDiagnosticNames.Metrics.MessagesRetried);
    private static readonly Counter<long> DeadLettered = Meter.CreateCounter<long>(TcjMessagingDiagnosticNames.Metrics.MessagesDeadLettered);
    private static readonly Histogram<double> PublishDuration = Meter.CreateHistogram<double>(TcjMessagingDiagnosticNames.Metrics.PublishDuration, "ms");
    private static readonly Histogram<double> ProcessingDuration = Meter.CreateHistogram<double>(TcjMessagingDiagnosticNames.Metrics.ProcessingDuration, "ms");
    private static readonly UpDownCounter<long> ActiveConsumers = Meter.CreateUpDownCounter<long>(TcjMessagingDiagnosticNames.Metrics.ActiveConsumers);

    public static Activity? StartPublish(TransportMessageEnvelope message, MessagingTransportDescriptor descriptor, string destination)
    {
        Activity? activity = ActivitySource.StartActivity(TcjMessagingDiagnosticNames.Activities.Publish, ActivityKind.Producer);
        SetBaseTags(activity, descriptor, destination, message, "publish");
        return activity;
    }

    public static Activity? StartReceive(TransportMessageEnvelope message, MessagingTransportDescriptor descriptor, string source)
    {
        Activity? activity = ActivitySource.StartActivity(TcjMessagingDiagnosticNames.Activities.Receive, ActivityKind.Consumer);
        SetBaseTags(activity, descriptor, source, message, "receive");
        return activity;
    }

    public static Activity? StartSettle(MessagingTransportDescriptor descriptor, string settlement)
    {
        Activity? activity = ActivitySource.StartActivity(TcjMessagingDiagnosticNames.Activities.Settle, ActivityKind.Consumer);
        activity?.SetTag(TcjMessagingDiagnosticNames.StandardAttributes.MessagingSystem, descriptor.Name);
        activity?.SetTag(TcjMessagingDiagnosticNames.StandardAttributes.OperationName, "settle");
        activity?.SetTag(TcjMessagingDiagnosticNames.Tags.Transport, descriptor.Name);
        activity?.SetTag(TcjMessagingDiagnosticNames.Tags.Settlement, Bound(settlement, 64));
        return activity;
    }

    public static Activity? StartConsumerExecute(TransportMessageEnvelope message, MessagingTransportDescriptor descriptor, string source)
    {
        Activity? activity = ActivitySource.StartActivity(TcjMessagingDiagnosticNames.Activities.ConsumerExecute, ActivityKind.Consumer);
        SetBaseTags(activity, descriptor, source, message, "process");
        return activity;
    }

    public static Activity? StartDeserialize(TransportMessageEnvelope message)
    {
        Activity? activity = ActivitySource.StartActivity(TcjMessagingDiagnosticNames.Activities.Deserialize, ActivityKind.Internal);
        activity?.SetTag(TcjMessagingDiagnosticNames.Tags.MessageType, Bound(message.MessageType, 128));
        activity?.SetTag(TcjMessagingDiagnosticNames.Tags.MessageVersion, message.MessageVersion);
        activity?.SetTag(TcjMessagingDiagnosticNames.Tags.Operation, "deserialize");
        return activity;
    }

    public static void CompleteDeserialize(Activity? activity, Exception? exception = null)
    {
        if (exception is null)
        {
            activity?.SetTag(TcjMessagingDiagnosticNames.Tags.Outcome, "success");
            activity?.SetStatus(ActivityStatusCode.Ok);
        }
        else
        {
            activity?.SetTag(TcjMessagingDiagnosticNames.Tags.Outcome, "failure");
            activity?.SetTag(TcjMessagingDiagnosticNames.Tags.FailureType, Bound(exception.GetType().Name, 128));
            activity?.SetStatus(ActivityStatusCode.Error, Bound(exception.GetType().Name, 128));
        }
    }

    public static void CompletePublish(Activity? activity, PublishResult result, double elapsedMilliseconds,
        MessagingTransportDescriptor descriptor, string destination, TransportMessageEnvelope message)
    {
        activity?.SetTag(TcjMessagingDiagnosticNames.Tags.Outcome, result.Outcome.ToString());
        if (result.FailureCategory is { } failure)
            activity?.SetTag(TcjMessagingDiagnosticNames.Tags.FailureType, failure.ToString());
        activity?.SetStatus(result.IsSuccess ? ActivityStatusCode.Ok : ActivityStatusCode.Error, result.IsSuccess ? null : result.Outcome.ToString());
        TagList tags = CreateTags(descriptor, destination, message, "publish");
        tags.Add(TcjMessagingDiagnosticNames.Tags.Outcome, result.Outcome.ToString());
        if (result.FailureCategory is { } category) tags.Add(TcjMessagingDiagnosticNames.Tags.FailureType, category.ToString());
        if (result.IsSuccess) Published.Add(1, tags);
        PublishDuration.Record(elapsedMilliseconds, tags);
    }

    public static void RecordReceived(MessagingTransportDescriptor descriptor, string source, TransportMessageEnvelope message) =>
        Received.Add(1, CreateTags(descriptor, source, message, "receive"));

    public static void RecordSettlement(MessagingTransportDescriptor descriptor, string source, TransportMessageEnvelope message, string settlement)
    {
        TagList tags = CreateTags(descriptor, source, message, "settle");
        tags.Add(TcjMessagingDiagnosticNames.Tags.Settlement, Bound(settlement, 64));
        if (string.Equals(settlement, "complete", StringComparison.OrdinalIgnoreCase)) Completed.Add(1, tags);
        else if (string.Equals(settlement, "retry", StringComparison.OrdinalIgnoreCase)) Retried.Add(1, tags);
        else if (string.Equals(settlement, "dead_letter", StringComparison.OrdinalIgnoreCase)) DeadLettered.Add(1, tags);
    }

    public static void RecordProcessingDuration(MessagingTransportDescriptor descriptor, string source, TransportMessageEnvelope message, string outcome, double elapsedMilliseconds)
    {
        TagList tags = CreateTags(descriptor, source, message, "process");
        tags.Add(TcjMessagingDiagnosticNames.Tags.Outcome, Bound(outcome, 64));
        ProcessingDuration.Record(elapsedMilliseconds, tags);
    }

    public static void ConsumerStarted() => ActiveConsumers.Add(1);
    public static void ConsumerStopped() => ActiveConsumers.Add(-1);

    private static void SetBaseTags(Activity? activity, MessagingTransportDescriptor descriptor, string destination, TransportMessageEnvelope message, string operation)
    {
        activity?.SetTag(TcjMessagingDiagnosticNames.StandardAttributes.MessagingSystem, Bound(descriptor.Name, 128));
        activity?.SetTag(TcjMessagingDiagnosticNames.StandardAttributes.DestinationName, Bound(destination, 128));
        activity?.SetTag(TcjMessagingDiagnosticNames.StandardAttributes.OperationName, operation);
        activity?.SetTag(TcjMessagingDiagnosticNames.Tags.Transport, Bound(descriptor.Name, 128));
        activity?.SetTag(TcjMessagingDiagnosticNames.Tags.Destination, Bound(destination, 128));
        activity?.SetTag(TcjMessagingDiagnosticNames.Tags.MessageType, Bound(message.MessageType, 128));
        activity?.SetTag(TcjMessagingDiagnosticNames.Tags.MessageVersion, message.MessageVersion);
        activity?.SetTag(TcjMessagingDiagnosticNames.Tags.Operation, operation);
    }

    private static TagList CreateTags(MessagingTransportDescriptor descriptor, string destination, TransportMessageEnvelope message, string operation)
    {
        // Metric dimensions must remain low-cardinality. Destination is intentionally
        // excluded because PublishContext can contain application-defined overrides.
        var tags = new TagList
        {
            { TcjMessagingDiagnosticNames.Tags.Transport, Bound(descriptor.Name, 128) },
            { TcjMessagingDiagnosticNames.Tags.MessageType, Bound(message.MessageType, 128) },
            { TcjMessagingDiagnosticNames.Tags.MessageVersion, message.MessageVersion },
            { TcjMessagingDiagnosticNames.Tags.Operation, operation }
        };
        return tags;
    }

    private static string Bound(string value, int maximum) => value.Length <= maximum ? value : value[..maximum];
}
