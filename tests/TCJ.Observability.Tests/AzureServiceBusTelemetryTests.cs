using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text;
using TCJ.Messaging.AzureServiceBus.Diagnostics;
using TCJ.Messaging.Envelopes;

namespace TCJ.Observability.Tests;

public sealed class AzureServiceBusTelemetryTests
{
    [Fact]
    public void Activities_use_stable_names_and_do_not_emit_sensitive_or_high_cardinality_identifiers()
    {
        using var collector = new ActivityCollector(TcjAzureServiceBusDiagnosticNames.ActivitySourceName);
        TransportMessageEnvelope envelope = Envelope();

        using (Activity? activity = AzureServiceBusDiagnostics.Start(
            TcjAzureServiceBusDiagnosticNames.PublishActivity,
            "publish",
            destination: "orders",
            entityType: "queue",
            sessionEnabled: true,
            message: envelope))
        {
            Assert.NotNull(activity);
            activity!.SetStatus(ActivityStatusCode.Ok);
        }

        Activity published = Assert.Single(collector.Activities);
        Assert.Equal(TcjAzureServiceBusDiagnosticNames.PublishActivity, published.OperationName);
        Assert.Equal("servicebus", published.GetTagItem(TcjAzureServiceBusDiagnosticNames.Tags.MessagingSystem));
        Assert.Equal("orders", published.GetTagItem(TcjAzureServiceBusDiagnosticNames.Tags.Destination));
        Assert.Equal("telemetry.message", published.GetTagItem(TcjAzureServiceBusDiagnosticNames.Tags.MessageType));
        Assert.Equal(1, published.GetTagItem(TcjAzureServiceBusDiagnosticNames.Tags.MessageVersion));

        string serializedTags = string.Join("\n", published.TagObjects.Select(static tag => $"{tag.Key}={tag.Value}"));
        Assert.DoesNotContain("logical-message-id-secret", serializedTags, StringComparison.Ordinal);
        Assert.DoesNotContain("session-secret", serializedTags, StringComparison.Ordinal);
        Assert.DoesNotContain("credential-secret", serializedTags, StringComparison.Ordinal);
    }

    [Fact]
    public void Metrics_have_stable_bounded_dimensions()
    {
        var measurements = new ConcurrentQueue<Measurement>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == TcjAzureServiceBusDiagnosticNames.MeterName)
                meterListener.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            measurements.Enqueue(new Measurement(instrument.Name, value, CopyTags(tags))));
        listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
            measurements.Enqueue(new Measurement(instrument.Name, value, CopyTags(tags))));
        listener.Start();

        AzureServiceBusDiagnostics.MessagePublished();
        AzureServiceBusDiagnostics.MessageScheduled();
        AzureServiceBusDiagnostics.MessageReceived();
        AzureServiceBusDiagnostics.MessageCompleted();
        AzureServiceBusDiagnostics.MessageAbandoned();
        AzureServiceBusDiagnostics.MessageDeferred();
        AzureServiceBusDiagnostics.MessageDeadLettered();
        AzureServiceBusDiagnostics.LockRenewed();
        AzureServiceBusDiagnostics.LockLost();
        AzureServiceBusDiagnostics.ProcessorError();
        AzureServiceBusDiagnostics.SessionAccepted();
        AzureServiceBusDiagnostics.SessionReleased();
        AzureServiceBusDiagnostics.RecordPublishDuration(1.25);
        AzureServiceBusDiagnostics.RecordProcessingDuration(2.5);

        string[] required =
        [
            TcjAzureServiceBusDiagnosticNames.Metrics.MessagesPublished,
            TcjAzureServiceBusDiagnosticNames.Metrics.MessagesScheduled,
            TcjAzureServiceBusDiagnosticNames.Metrics.MessagesReceived,
            TcjAzureServiceBusDiagnosticNames.Metrics.MessagesCompleted,
            TcjAzureServiceBusDiagnosticNames.Metrics.MessagesAbandoned,
            TcjAzureServiceBusDiagnosticNames.Metrics.MessagesDeferred,
            TcjAzureServiceBusDiagnosticNames.Metrics.MessagesDeadLettered,
            TcjAzureServiceBusDiagnosticNames.Metrics.LockRenewals,
            TcjAzureServiceBusDiagnosticNames.Metrics.LockLosses,
            TcjAzureServiceBusDiagnosticNames.Metrics.ProcessorErrors,
            TcjAzureServiceBusDiagnosticNames.Metrics.ActiveSessions,
            TcjAzureServiceBusDiagnosticNames.Metrics.PublishDuration,
            TcjAzureServiceBusDiagnosticNames.Metrics.ProcessingDuration
        ];
        Assert.All(required, name => Assert.Contains(measurements, measurement => measurement.Name == name));
        Assert.All(measurements, measurement =>
        {
            Assert.Single(measurement.Tags);
            Assert.Equal("servicebus", measurement.Tags[TcjAzureServiceBusDiagnosticNames.Tags.MessagingSystem]);
        });
    }

    [Fact]
    public void Malformed_trace_context_is_ignored_without_throwing()
    {
        TransportMessageEnvelope envelope = Envelope(new Dictionary<string, string>
        {
            ["traceparent"] = "not-a-w3c-traceparent",
            ["tracestate"] = "vendor=state"
        });

        ActivityContext context = AzureServiceBusDiagnostics.ExtractParent(envelope);

        Assert.Equal(default, context);
    }

    private static TransportMessageEnvelope Envelope(IReadOnlyDictionary<string, string>? headers = null) => new(
        "logical-message-id-secret",
        "telemetry.message",
        1,
        Encoding.UTF8.GetBytes("{\"value\":1}"),
        "application/json",
        DateTimeOffset.UnixEpoch,
        correlationId: "correlation",
        causationId: "causation",
        orderingKey: "session-secret",
        headers: headers ?? new Dictionary<string, string> { ["x-secret"] = "credential-secret" });

    private static IReadOnlyDictionary<string, object?> CopyTags(ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (KeyValuePair<string, object?> tag in tags) result[tag.Key] = tag.Value;
        return result;
    }

    private sealed record Measurement(string Name, double Value, IReadOnlyDictionary<string, object?> Tags);
}
