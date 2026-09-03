using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text;
using TCJ.Messaging.Diagnostics;
using TCJ.Messaging.Envelopes;
using TCJ.Messaging.Publishing;
using TCJ.Messaging.Receiving;

namespace TCJ.Messaging.ConformanceTests;

/// <summary>
/// Reusable adapter conformance suite. Future transport adapters should derive from this fixture
/// and provide deterministic fault/delay hooks for their test transport boundary.
/// </summary>
public abstract class MessagingAdapterConformanceTests
{
    protected abstract ValueTask<MessagingAdapterHarness> CreateHarnessAsync();
    protected abstract void EnqueuePublishResult(MessagingAdapterHarness harness, PublishResult result);
    protected abstract void SetPublishDelay(MessagingAdapterHarness harness, TimeSpan delay);
    protected abstract void AdvanceTime(MessagingAdapterHarness harness, TimeSpan duration);
    protected abstract Task InjectDuplicateAsync(
        MessagingAdapterHarness harness,
        TransportMessageEnvelope message,
        CancellationToken cancellationToken = default);
    protected abstract void SetAvailability(MessagingAdapterHarness harness, bool available);

    [Fact]
    public async Task Adapter_declares_bounded_capabilities()
    {
        await using MessagingAdapterHarness harness = await CreateHarnessAsync();
        MessagingTransportDescriptor descriptor = harness.Descriptor;

        Assert.False(string.IsNullOrWhiteSpace(descriptor.Name));
        Assert.False(string.IsNullOrWhiteSpace(descriptor.Version));
        Assert.NotNull(descriptor.Capabilities);
        if (descriptor.Capabilities.MaximumPayloadBytes is int payloadLimit)
            Assert.True(payloadLimit > 0);
        if (descriptor.Capabilities.MaximumHeaderBytes is int headerLimit)
            Assert.True(headerLimit > 0);
        if (descriptor.Capabilities.MaximumBatchSize is int batchLimit)
            Assert.True(batchLimit > 0);
    }

    [Fact]
    public async Task Publish_and_receive_preserve_stable_message_identity_and_metadata()
    {
        await using MessagingAdapterHarness harness = await CreateHarnessAsync();
        TransportMessageEnvelope message = CreateEnvelope(
            id: "stable-message-id",
            correlationId: "correlation-1",
            causationId: "cause-1",
            headers: new Dictionary<string, string> { ["custom-safe"] = "value" });

        PublishResult result = await harness.Publisher.PublishAsync(
            message,
            new PublishContext { Destination = harness.Source });

        Assert.True(result.IsSuccess);
        await using IAsyncEnumerator<ReceivedMessage> receiver = harness.Receiver
            .ReceiveAsync(new ReceiveContext { Source = harness.Source })
            .GetAsyncEnumerator();
        Assert.True(await receiver.MoveNextAsync());
        ReceivedMessage received = receiver.Current;
        Assert.Equal(message.MessageId, received.Envelope.MessageId);
        Assert.Equal(message.MessageType, received.Envelope.MessageType);
        Assert.Equal(message.MessageVersion, received.Envelope.MessageVersion);
        Assert.Equal("application/json", received.Envelope.ContentType);
        Assert.Equal("correlation-1", received.Envelope.CorrelationId);
        Assert.Equal("cause-1", received.Envelope.CausationId);
        Assert.Equal("value", received.Envelope.Headers["custom-safe"]);
        await received.Settlement.CompleteAsync();
    }

    [Fact]
    public async Task Forbidden_headers_are_removed_before_adapter_delivery()
    {
        await using MessagingAdapterHarness harness = await CreateHarnessAsync();
        TransportMessageEnvelope message = CreateEnvelope(headers: new Dictionary<string, string>
        {
            ["custom-safe"] = "safe",
            ["authorization"] = "Bearer should-not-propagate",
            ["x-api-key"] = "should-not-propagate"
        });

        PublishResult result = await harness.Publisher.PublishAsync(
            message,
            new PublishContext { Destination = harness.Source });
        Assert.True(result.IsSuccess);

        await using IAsyncEnumerator<ReceivedMessage> receiver = harness.Receiver
            .ReceiveAsync(new ReceiveContext { Source = harness.Source })
            .GetAsyncEnumerator();
        Assert.True(await receiver.MoveNextAsync());
        Assert.Equal("safe", receiver.Current.Envelope.Headers["custom-safe"]);
        Assert.False(receiver.Current.Envelope.Headers.ContainsKey("authorization"));
        Assert.False(receiver.Current.Envelope.Headers.ContainsKey("x-api-key"));
        await receiver.Current.Settlement.CompleteAsync();
    }

    [Fact]
    public async Task Receiver_respects_cancellation()
    {
        await using MessagingAdapterHarness harness = await CreateHarnessAsync();
        using var cancellation = new CancellationTokenSource();
        await using IAsyncEnumerator<ReceivedMessage> receiver = harness.Receiver
            .ReceiveAsync(new ReceiveContext { Source = harness.Source }, cancellation.Token)
            .GetAsyncEnumerator(cancellation.Token);

        Task<bool> pending = receiver.MoveNextAsync().AsTask();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    [Fact]
    public async Task Publish_timeout_is_classified_as_retryable_timeout()
    {
        await using MessagingAdapterHarness harness = await CreateHarnessAsync();
        SetPublishDelay(harness, TimeSpan.FromMinutes(1));

        Task<PublishResult> publish = harness.Publisher.PublishAsync(
            CreateEnvelope(),
            new PublishContext { Destination = harness.Source });
        AdvanceTime(harness, TimeSpan.FromSeconds(31));

        PublishResult result = await publish;
        Assert.Equal(PublishOutcome.TimedOut, result.Outcome);
        Assert.Equal(MessagingFailureCategory.TransientTimeout, result.FailureCategory);
        Assert.True(result.IsRetryable);
    }

    [Fact]
    public async Task Transient_failure_is_classified_retryable()
    {
        await using MessagingAdapterHarness harness = await CreateHarnessAsync();
        EnqueuePublishResult(
            harness,
            new PublishResult(
                PublishOutcome.TransientFailure,
                FailureCategory: MessagingFailureCategory.TransientConnection,
                FailureType: "TransientConnection"));

        PublishResult result = await harness.Publisher.PublishAsync(
            CreateEnvelope(),
            new PublishContext { Destination = harness.Source });

        Assert.Equal(PublishOutcome.TransientFailure, result.Outcome);
        Assert.True(result.IsRetryable);
    }

    [Fact]
    public async Task Permanent_failure_is_not_retryable()
    {
        await using MessagingAdapterHarness harness = await CreateHarnessAsync();
        EnqueuePublishResult(
            harness,
            new PublishResult(
                PublishOutcome.PermanentFailure,
                FailureCategory: MessagingFailureCategory.PermanentTopology,
                FailureType: "PermanentTopology"));

        PublishResult result = await harness.Publisher.PublishAsync(
            CreateEnvelope(),
            new PublishContext { Destination = harness.Source });

        Assert.Equal(PublishOutcome.PermanentFailure, result.Outcome);
        Assert.False(result.IsRetryable);
    }

    [Fact]
    public async Task Retry_redelivery_preserves_message_id_and_increments_attempt()
    {
        await using MessagingAdapterHarness harness = await CreateHarnessAsync();
        await harness.Publisher.PublishAsync(
            CreateEnvelope(id: "retry-stable"),
            new PublishContext { Destination = harness.Source });

        await using IAsyncEnumerator<ReceivedMessage> receiver = harness.Receiver
            .ReceiveAsync(new ReceiveContext { Source = harness.Source })
            .GetAsyncEnumerator();
        Assert.True(await receiver.MoveNextAsync());
        Assert.Equal(1, receiver.Current.Delivery.DeliveryAttempt);
        await receiver.Current.Settlement.RetryAsync(new RetrySettlementOptions());

        Assert.True(await receiver.MoveNextAsync());
        Assert.Equal("retry-stable", receiver.Current.Envelope.MessageId);
        Assert.Equal(2, receiver.Current.Delivery.DeliveryAttempt);
        await receiver.Current.Settlement.CompleteAsync();
    }

    [Fact]
    public async Task Duplicate_delivery_preserves_same_logical_message_id()
    {
        await using MessagingAdapterHarness harness = await CreateHarnessAsync();
        TransportMessageEnvelope message = CreateEnvelope(id: "duplicate-stable");
        await InjectDuplicateAsync(harness, message);

        await using IAsyncEnumerator<ReceivedMessage> receiver = harness.Receiver
            .ReceiveAsync(new ReceiveContext { Source = harness.Source })
            .GetAsyncEnumerator();
        Assert.True(await receiver.MoveNextAsync());
        string first = receiver.Current.Envelope.MessageId;
        await receiver.Current.Settlement.CompleteAsync();
        Assert.True(await receiver.MoveNextAsync());
        string second = receiver.Current.Envelope.MessageId;
        await receiver.Current.Settlement.CompleteAsync();

        Assert.Equal("duplicate-stable", first);
        Assert.Equal(first, second);
    }

    [Fact]
    public async Task Dead_letter_capability_matches_runtime_behavior()
    {
        await using MessagingAdapterHarness harness = await CreateHarnessAsync();
        await harness.Publisher.PublishAsync(
            CreateEnvelope(),
            new PublishContext { Destination = harness.Source });
        await using IAsyncEnumerator<ReceivedMessage> receiver = harness.Receiver
            .ReceiveAsync(new ReceiveContext { Source = harness.Source })
            .GetAsyncEnumerator();
        Assert.True(await receiver.MoveNextAsync());

        if (harness.Descriptor.Capabilities.SupportsDeadLetter)
        {
            await receiver.Current.Settlement.DeadLetterAsync(new DeadLetterOptions { Reason = "permanent" });
        }
        else
        {
            await Assert.ThrowsAsync<MessagingCapabilityException>(() =>
                receiver.Current.Settlement.DeadLetterAsync(new DeadLetterOptions { Reason = "permanent" }));
        }
    }

    [Fact]
    public async Task Health_probe_tracks_transport_availability_without_leaking_details()
    {
        await using MessagingAdapterHarness harness = await CreateHarnessAsync();
        SetAvailability(harness, true);
        Assert.True(await harness.HealthProbe.IsReadyAsync());
        SetAvailability(harness, false);
        Assert.False(await harness.HealthProbe.IsReadyAsync());
    }

    [Fact]
    public async Task Publish_emits_bounded_activity_and_metrics()
    {
        await using MessagingAdapterHarness harness = await CreateHarnessAsync();
        var activities = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == TcjMessagingDiagnosticNames.Source,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => activities.Add(activity)
        };
        ActivitySource.AddActivityListener(listener);

        var metricNames = new HashSet<string>(StringComparer.Ordinal);
        using var meterListener = new MeterListener();
        meterListener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == TcjMessagingDiagnosticNames.Source)
                listener.EnableMeasurementEvents(instrument);
        };
        meterListener.SetMeasurementEventCallback<long>((instrument, _, _, _) => metricNames.Add(instrument.Name));
        meterListener.SetMeasurementEventCallback<double>((instrument, _, _, _) => metricNames.Add(instrument.Name));
        meterListener.Start();

        PublishResult result = await harness.Publisher.PublishAsync(
            CreateEnvelope(),
            new PublishContext { Destination = harness.Source });
        Assert.True(result.IsSuccess);
        Assert.Contains(activities, activity => activity.OperationName == TcjMessagingDiagnosticNames.Activities.Publish);
        Assert.Contains(TcjMessagingDiagnosticNames.Metrics.MessagesPublished, metricNames);
        Assert.Contains(TcjMessagingDiagnosticNames.Metrics.PublishDuration, metricNames);
    }

    [Fact]
    public async Task Metric_dimensions_exclude_application_defined_destination()
    {
        await using MessagingAdapterHarness harness = await CreateHarnessAsync();
        var metricTagNames = new HashSet<string>(StringComparer.Ordinal);
        using var meterListener = new MeterListener();
        meterListener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == TcjMessagingDiagnosticNames.Source)
                listener.EnableMeasurementEvents(instrument);
        };
        meterListener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            foreach (KeyValuePair<string, object?> tag in tags)
                metricTagNames.Add(tag.Key);
        });
        meterListener.SetMeasurementEventCallback<double>((_, _, tags, _) =>
        {
            foreach (KeyValuePair<string, object?> tag in tags)
                metricTagNames.Add(tag.Key);
        });
        meterListener.Start();

        PublishResult result = await harness.Publisher.PublishAsync(
            CreateEnvelope(),
            new PublishContext { Destination = harness.Source + ".tenant-123" });

        Assert.True(result.IsSuccess);
        Assert.DoesNotContain(TcjMessagingDiagnosticNames.Tags.Destination, metricTagNames);
    }

    [Fact]
    public async Task Transport_unavailability_is_reported_as_transient_failure()
    {
        await using MessagingAdapterHarness harness = await CreateHarnessAsync();
        SetAvailability(harness, false);

        PublishResult result = await harness.Publisher.PublishAsync(
            CreateEnvelope(),
            new PublishContext { Destination = harness.Source });

        Assert.Equal(PublishOutcome.TransientFailure, result.Outcome);
        Assert.True(result.IsRetryable);
    }

    private static TransportMessageEnvelope CreateEnvelope(
        string id = "conformance-message",
        string? correlationId = null,
        string? causationId = null,
        IReadOnlyDictionary<string, string>? headers = null) =>
        new(
            id,
            "conformance.message",
            1,
            Encoding.UTF8.GetBytes("{\"value\":\"ok\"}"),
            "application/json",
            DateTimeOffset.Parse("2026-09-02T00:00:00Z"),
            correlationId,
            causationId,
            headers: headers);
}
