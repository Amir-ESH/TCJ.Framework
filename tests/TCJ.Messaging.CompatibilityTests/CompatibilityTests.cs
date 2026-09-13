using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using TCJ.Core.DomainEvents;
using TCJ.Core.Inbox;
using TCJ.Core.Outbox;
using TCJ.Core.Resilience;
using TCJ.Messaging.Configuration;
using TCJ.Messaging.ConformanceTests;
using TCJ.Messaging.Diagnostics;
using TCJ.Messaging.Envelopes;
using TCJ.Messaging.Extensions;
using TCJ.Messaging.Integration;
using TCJ.Messaging.Publishing;
using TCJ.Messaging.Receiving;

[assembly: CollectionBehavior(DisableTestParallelization = true)]
namespace TCJ.Messaging.CompatibilityTests;

[Trait("Category", "MessagingCompatibility")]
public sealed class CompatibilityTests
{
    [Fact, Trait("Transport", "InMemory")] public Task InMemory() => RunAsync("InMemory");
    [Fact, Trait("Transport", "RabbitMQ")] public Task RabbitMQ() => RunAsync("RabbitMQ");
    [Fact, Trait("Transport", "AzureServiceBus")] public Task AzureServiceBus() => RunAsync("AzureServiceBus");
    [Fact, Trait("Transport", "Kafka")] public Task Kafka() => RunAsync("Kafka");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, Converters = { new JsonStringEnumConverter() } };

    private static async Task RunAsync(string transport)
    {
        string directory = Environment.GetEnvironmentVariable("TCJ_MESSAGING_COMPATIBILITY_RESULTS")
            ?? Path.Combine(Directory.GetCurrentDirectory(), "TestResults", "MessagingCompatibility");
        Directory.CreateDirectory(directory);
        var results = new List<ScenarioResult>();
        MessagingTransportDescriptor? descriptor = null;
        await using var fixture = new CompatibilityFixture(transport);
        try
        {
            await fixture.InitializeAsync();
            await using (var h = await fixture.CreateAsync()) descriptor = h.Descriptor;
            var common = ReusableMessagingScenarios.Create(() => fixture.CreateAsync());
            await Record("descriptor", common.Adapter_declares_bounded_capabilities);
            await Record("identity", common.Publish_and_receive_preserve_stable_message_identity_and_metadata);
            await Record("headers", common.Forbidden_headers_are_removed_before_adapter_delivery);
            await Record("receive-cancellation", common.Receiver_respects_cancellation);
            await Record("publish-telemetry", common.Publish_emits_bounded_activity_and_metrics);
            await Record("neutral-runner-telemetry", () => NeutralTelemetry(fixture));
            await Record("logical-body", () => LogicalBody(fixture));
            await Record("publish-cancellation", () => PublishCancellation(fixture));
            await Record("payload-limit", () => PayloadLimit(fixture));
            await Record("header-limit", () => HeaderLimit(fixture));
            await Record("batch", () => Batch(fixture));
            await Record("unsupported-options", () => UnsupportedOptions(fixture));
            await Record("settlement", () => Settlement(fixture));
            await Record("inbox-order", () => Inbox(fixture, fail: false));
            await Record("inbox-failure", () => Inbox(fixture, fail: true));
            await Record("outbox-retry", () => OutboxRetry(fixture));
            await Record("readiness", () => Readiness(fixture));
            await Record("startup-diagnostics", () => StartupDiagnostics(fixture));
            await Record("ordering", () => Ordering(fixture));
        }
        catch (Exception)
        {
            // Deliberately exclude broker exception text, endpoints, payloads and credentials.
            results.Add(new("fixture", "Blocked", "Failed", 0));
        }
        finally
        {
            string contextFile = Path.Combine(directory, "context.json");
            JsonElement? context = File.Exists(contextFile) ? JsonSerializer.Deserialize<JsonElement>(await File.ReadAllTextAsync(contextFile)) : null;
            var evidence = new { schemaVersion = 1, transport, descriptor, context, scenarios = results, overall = results.Count > 0 && results.All(r => r.Outcome == "Passed") ? "pass" : "fail" };
            await File.WriteAllTextAsync(Path.Combine(directory, transport + ".json"), JsonSerializer.Serialize(evidence, JsonOptions));
        }
        Assert.True(results.Count > 0 && results.All(r => r.Outcome == "Passed"), "Compatibility failed: " + string.Join(", ", results.Where(r => r.Outcome != "Passed").Select(r => r.Scenario)));

        async Task Record(string scenario, Func<Task> execute)
        {
            var timer = Stopwatch.StartNew();
            try { await execute().WaitAsync(TimeSpan.FromSeconds(45)); results.Add(new(scenario, "Supported", "Passed", timer.ElapsedMilliseconds)); }
            catch (Exception) { results.Add(new(scenario, "Blocked", "Failed", timer.ElapsedMilliseconds)); }
        }
    }

    private static TransportMessageEnvelope Envelope(string id = "compatibility", byte[]? body = null) =>
        new(id, "compatibility.message", 1, body ?? Encoding.UTF8.GetBytes("{\"value\":1}"), "application/json", DateTimeOffset.UtcNow, "correlation", "causation");
    private static PublishContext Context(MessagingAdapterHarness h) => new() { Destination = h.PublishDestination };
    private static IAsyncEnumerator<ReceivedMessage> Receive(MessagingAdapterHarness h, CancellationToken token) =>
        h.Receiver.ReceiveAsync(new ReceiveContext { Source = h.Source, Subscription = h.Subscription }, token).GetAsyncEnumerator(token);

    private static async Task LogicalBody(CompatibilityFixture fixture)
    {
        await using var h = await fixture.CreateAsync(); using var c = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var message = Envelope(); Assert.True((await h.Publisher.PublishAsync(message, Context(h), c.Token)).IsSuccess);
        await using var receiver = Receive(h, c.Token); Assert.True(await receiver.MoveNextAsync());
        using var expected = JsonDocument.Parse(message.Body); using var actual = JsonDocument.Parse(receiver.Current.Envelope.Body);
        Assert.True(JsonElement.DeepEquals(expected.RootElement, actual.RootElement));
        await receiver.Current.Settlement.CompleteAsync(c.Token);
    }

    private static async Task PublishCancellation(CompatibilityFixture fixture)
    {
        await using var h = await fixture.CreateAsync(); using var c = new CancellationTokenSource(); c.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Publisher.PublishAsync(Envelope(), Context(h), c.Token));
    }

    private static async Task PayloadLimit(CompatibilityFixture fixture)
    {
        await using var h = await fixture.CreateAsync();
        var result = await h.Publisher.PublishAsync(Envelope(body: new byte[1024 * 1024 + 1]), Context(h));
        Assert.Equal(MessagingFailureCategory.PayloadTooLarge, result.FailureCategory); Assert.False(result.IsRetryable); Assert.False(result.IsSuccess);
    }

    private static async Task HeaderLimit(CompatibilityFixture fixture)
    {
        await using var h = await fixture.CreateAsync(services =>
        {
            var options = (TcjMessagingOptions)Assert.Single(services.Where(s => s.ServiceType == typeof(TcjMessagingOptions))).ImplementationInstance!;
            options.MaximumHeaderValueLength = 64;
        });
        var envelope = new TransportMessageEnvelope("header-limit", "compatibility.message", 1, Encoding.UTF8.GetBytes("{}"), "application/json", DateTimeOffset.UtcNow,
            headers: new Dictionary<string, string> { ["custom-safe"] = new string('x', 65) });
        var result = await h.Publisher.PublishAsync(envelope, Context(h));
        Assert.Equal(PublishOutcome.PermanentFailure, result.Outcome);
        Assert.Equal(MessagingFailureCategory.PermanentSerialization, result.FailureCategory);
    }

    private static async Task OutboxRetry(CompatibilityFixture fixture)
    {
        Guid identity = Guid.NewGuid();
        var gate = new TaskCompletionSource<PublishResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        PublishProbe? probe = null;
        await using var h = await fixture.CreateAsync(services =>
        {
            services.AddSingleton<IDomainEventDispatcher, LocalDispatcher>();
            services.AddSingleton<IOutboxMessageContextAccessor>(new OutboxContext { Current = new OutboxMessageContext(identity, "compatibility.message.v1", 1, "correlation", "causation", null) });
            services.AddTcjMessage("compatibility.message", 1, CompatibilityJsonContext.Default.CompatibilityEvent);
            var adapter = Assert.Single(services.Where(d => d.ServiceType == typeof(IMessagingTransportPublisher)));
            services.Remove(adapter);
            services.AddSingleton<IMessagingTransportPublisher>(sp => probe = new PublishProbe((IMessagingTransportPublisher)adapter.ImplementationFactory!(sp), gate.Task));
            services.AddTcjMessagingOutboxBridge();
        });
        var dispatcher = fixture.Services.GetRequiredService<IDomainEventDispatcher>();
        IDomainEvent[] events = [new CompatibilityEvent("safe", DateTimeOffset.UtcNow)];
        Task first = dispatcher.DispatchAsync(events);
        Assert.False(first.IsCompleted);
        gate.SetResult(new PublishResult(PublishOutcome.TransientFailure, FailureCategory: MessagingFailureCategory.TransientConnection));
        Exception error = await Assert.ThrowsAnyAsync<Exception>(() => first);
        Assert.Contains(fixture.Services.GetServices<ITransientFailureClassifier>(), classifier => classifier.IsTransient(error));
        await dispatcher.DispatchAsync(events);
        Assert.NotNull(probe); Assert.Equal(new[] { identity.ToString("D"), identity.ToString("D") }, probe.Identities);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var receiver = Receive(h, cancellation.Token); Assert.True(await receiver.MoveNextAsync());
        Assert.Equal(identity.ToString("D"), receiver.Current.Envelope.MessageId);
        await receiver.Current.Settlement.CompleteAsync(cancellation.Token);
    }

    private static async Task Batch(CompatibilityFixture fixture)
    {
        await using var h = await fixture.CreateAsync();
        var results = await h.BatchPublisher.PublishBatchAsync([Envelope("first"), Envelope("invalid", new byte[1024 * 1024 + 1]), Envelope("last")], Context(h));
        Assert.Equal(3, results.Count);
        if (h.Descriptor.Capabilities.SupportsBatchPublish)
        {
            Assert.True(results[0].IsSuccess); Assert.Equal(MessagingFailureCategory.PayloadTooLarge, results[1].FailureCategory); Assert.True(results[2].IsSuccess);
            using var c = new CancellationTokenSource(TimeSpan.FromSeconds(20)); await using var receiver = Receive(h, c.Token);
            var ids = new HashSet<string>();
            for (int i = 0; i < 2; i++) { Assert.True(await receiver.MoveNextAsync()); ids.Add(receiver.Current.Envelope.MessageId); await receiver.Current.Settlement.CompleteAsync(c.Token); }
            Assert.Equal(2, ids.Count); Assert.Contains("first", ids); Assert.Contains("last", ids);
        }
        else Assert.All(results, r => Assert.Equal(PublishOutcome.UnsupportedCapability, r.Outcome));
    }

    private static async Task UnsupportedOptions(CompatibilityFixture fixture)
    {
        await using var h = await fixture.CreateAsync(); var c = h.Descriptor.Capabilities;
        var unsupported = new List<PublishContext>();
        if (!c.SupportsScheduling) unsupported.Add(Context(h) with { ScheduledAtUtc = DateTimeOffset.UtcNow.AddMinutes(1) });
        if (!c.SupportsTimeToLive) unsupported.Add(Context(h) with { TimeToLive = TimeSpan.FromSeconds(10) });
        if (!c.SupportsPartitioning) unsupported.Add(Context(h) with { PartitionKey = "partition" });
        if (!c.SupportsOrderedDelivery) unsupported.Add(Context(h) with { OrderingKey = "ordering" });
        foreach (var context in unsupported)
        {
            var result = await h.Publisher.PublishAsync(Envelope(), context);
            Assert.Equal(PublishOutcome.UnsupportedCapability, result.Outcome); Assert.Equal(MessagingFailureCategory.UnsupportedCapability, result.FailureCategory);
        }
        // Transactions have no neutral operation. No adapter may advertise them in this contract.
        Assert.False(c.SupportsTransactions);
    }

    private static async Task Settlement(CompatibilityFixture fixture)
    {
        await using var h = await fixture.CreateAsync(); using var c = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await h.Publisher.PublishAsync(Envelope(), Context(h), c.Token); await using var receiver = Receive(h, c.Token); Assert.True(await receiver.MoveNextAsync());
        var settlement = receiver.Current.Settlement;
        if (!h.Descriptor.Capabilities.SupportsDefer) await Assert.ThrowsAsync<MessagingCapabilityException>(() => settlement.DeferAsync(c.Token));
        if (fixture.Transport == "Kafka") await Assert.ThrowsAsync<MessagingCapabilityException>(() => settlement.AbandonAsync(c.Token));
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => settlement.CompleteAsync(canceled.Token));
        await settlement.CompleteAsync(c.Token);
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => settlement.CompleteAsync(c.Token));
    }

    private static async Task Inbox(CompatibilityFixture fixture, bool fail)
    {
        await using var h = await fixture.CreateAsync(); using var c = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await h.Publisher.PublishAsync(Envelope(), Context(h), c.Token); await using var receiver = Receive(h, c.Token); Assert.True(await receiver.MoveNextAsync());
        var gate = new TaskCompletionSource<InboxHandlingResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pipeline = new Pipeline(gate.Task);
        var bridge = new InboxTransportBridge(pipeline, new TcjInboxOptions { ConsumerName = "compatibility" }, new TcjMessagingOptions(), h.Descriptor, new MessagingHeaderPolicy(new TcjMessagingOptions()), TimeProvider.System);
        var pending = bridge.ProcessAsync(receiver.Current, c.Token); Assert.False(pending.IsCompleted);
        if (fail)
        {
            gate.SetException(new InvalidOperationException("transaction failed")); await Assert.ThrowsAsync<InvalidOperationException>(() => pending);
            // Completion still being possible proves the failed bridge did not falsely settle it.
            await receiver.Current.Settlement.CompleteAsync(c.Token);
        }
        else { gate.SetResult(new InboxHandlingResult(InboxHandlingOutcome.Acknowledge)); Assert.Equal(MessageSettlement.Complete, (await pending).Settlement); }
        Assert.Equal("compatibility", pipeline.Identity);
    }

    private static async Task Readiness(CompatibilityFixture fixture)
    {
        await using var h = await fixture.CreateAsync(); using var c = new CancellationTokenSource(TimeSpan.FromSeconds(15)); Assert.True(await h.HealthProbe.IsReadyAsync(c.Token));
        c.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.HealthProbe.IsReadyAsync(c.Token).AsTask());
    }

    private static async Task StartupDiagnostics(CompatibilityFixture fixture)
    {
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await using var h = await fixture.CreateAsync(services => services.AddSingleton(new MessagingTransportDescriptor
            {
                Name = "duplicate", Version = "1", Capabilities = new MessagingTransportCapabilities()
            }));
            await h.Publisher.PublishAsync(Envelope(), Context(h));
        });
    }

    private static async Task NeutralTelemetry(CompatibilityFixture fixture)
    {
        await using var h = await fixture.CreateAsync();
        var activities = new ConcurrentQueue<Activity>();
        var measures = new ConcurrentDictionary<string, bool>();
        var metricTags = new ConcurrentQueue<KeyValuePair<string, object?>>();
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var activityListener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == TcjMessagingDiagnosticNames.Source,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activities.Enqueue
        };
        ActivitySource.AddActivityListener(activityListener);
        using var meter = new MeterListener();
        meter.InstrumentPublished = (instrument, listener) => { if (instrument.Meter.Name == TcjMessagingDiagnosticNames.Source) listener.EnableMeasurementEvents(instrument); };
        meter.SetMeasurementEventCallback<long>((instrument, _, tags, _) =>
        {
            measures[instrument.Name] = true;
            foreach (var tag in tags) metricTags.Enqueue(tag);
            if (instrument.Name == TcjMessagingDiagnosticNames.Metrics.MessagesCompleted) completed.TrySetResult();
        });
        meter.SetMeasurementEventCallback<double>((instrument, _, tags, _) => { measures[instrument.Name] = true; foreach (var tag in tags) metricTags.Enqueue(tag); });
        meter.Start();
        using var stopping = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var options = new TcjMessagingOptions { ShutdownTimeout = TimeSpan.FromSeconds(5) };
        var bridge = new InboxTransportBridge(new Pipeline(Task.FromResult(new InboxHandlingResult(InboxHandlingOutcome.Acknowledge))), new TcjInboxOptions { ConsumerName = "telemetry" }, options, h.Descriptor, new MessagingHeaderPolicy(options), TimeProvider.System);
        // Exercise the public neutral runner with the real receiver/settlement. Adapter-specific
        // runners remain covered by their independent suites and may add their own telemetry.
        var runner = new MessageConsumerRunner(h.Receiver, bridge, options, fixture.Services.GetRequiredService<IMessagingStartupValidator>(), new MessagingConsumerState(), h.Descriptor, TimeProvider.System);
        var secretBody = Encoding.UTF8.GetBytes("{\"value\":\"tcj-compat-secret-payload\"}");
        Assert.True((await h.Publisher.PublishAsync(Envelope(body: secretBody), Context(h), stopping.Token)).IsSuccess);
        Task running = runner.RunAsync(new ReceiveContext { Source = h.Source, Subscription = h.Subscription }, stopping.Token);
        try { await completed.Task.WaitAsync(stopping.Token); }
        finally { stopping.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(10)); }
        foreach (string activity in new[] { TcjMessagingDiagnosticNames.Activities.Publish, TcjMessagingDiagnosticNames.Activities.Receive, TcjMessagingDiagnosticNames.Activities.Settle, TcjMessagingDiagnosticNames.Activities.ConsumerExecute })
            Assert.Contains(activities, a => a.OperationName == activity);
        foreach (string metric in new[] { TcjMessagingDiagnosticNames.Metrics.MessagesPublished, TcjMessagingDiagnosticNames.Metrics.MessagesReceived, TcjMessagingDiagnosticNames.Metrics.MessagesCompleted, TcjMessagingDiagnosticNames.Metrics.PublishDuration, TcjMessagingDiagnosticNames.Metrics.ProcessingDuration, TcjMessagingDiagnosticNames.Metrics.ActiveConsumers })
            Assert.Contains(metric, measures.Keys);
        Assert.DoesNotContain(metricTags, t => t.Key.Contains("destination", StringComparison.OrdinalIgnoreCase) || t.Key.Contains("message_id", StringComparison.OrdinalIgnoreCase) || t.Key.Contains("correlation", StringComparison.OrdinalIgnoreCase));
        Assert.All(activities.SelectMany(a => a.TagObjects).Concat(metricTags), tag => Assert.DoesNotContain("tcj-compat-secret", tag.Value?.ToString() ?? "", StringComparison.Ordinal));
    }

    private static async Task Ordering(CompatibilityFixture fixture)
    {
        await using var h = await fixture.CreateAsync(); var guarantee = h.Descriptor.Capabilities.OrderingGuarantee;
        if (guarantee == MessagingOrderingGuarantee.None) { Assert.False(h.Descriptor.Capabilities.SupportsOrderedDelivery); return; }
        // Service Bus session ordering is exercised by the existing session integration scenario;
        // the verifier requires that named evidence, never a queue-order test as proof of sessions.
        if (guarantee == MessagingOrderingGuarantee.PerSession) { Assert.Equal("AzureServiceBus", fixture.Transport); return; }
        using var c = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        for (int i = 0; i < 3; i++) Assert.True((await h.Publisher.PublishAsync(Envelope("order-" + i), Context(h), c.Token)).IsSuccess);
        await using var receiver = Receive(h, c.Token);
        for (int i = 0; i < 3; i++) { Assert.True(await receiver.MoveNextAsync()); Assert.Equal("order-" + i, receiver.Current.Envelope.MessageId); await receiver.Current.Settlement.CompleteAsync(c.Token); }
    }

    private sealed class Pipeline(Task<InboxHandlingResult> result) : IInboxPipeline
    {
        internal string? Identity { get; private set; }
        public Task<InboxHandlingResult> ProcessAsync(IncomingMessageEnvelope envelope, CancellationToken cancellationToken = default) { Identity = envelope.MessageId; return result; }
    }
    private sealed record ScenarioResult(string Scenario, string ActualStatus, string Outcome, long DurationMs);
    private sealed class OutboxContext : IOutboxMessageContextAccessor { public OutboxMessageContext? Current { get; init; } }
    private sealed class LocalDispatcher : IDomainEventDispatcher
    {
        public Task DispatchAsync(IReadOnlyCollection<IDomainEvent> events, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Outbox cannot fall back to local dispatch.");
    }
    private sealed class PublishProbe(IMessagingTransportPublisher adapter, Task<PublishResult> first) : IMessagingTransportPublisher
    {
        internal List<string> Identities { get; } = [];
        public Task<PublishResult> PublishAsync(TransportMessageEnvelope message, PublishContext context, CancellationToken cancellationToken = default)
        {
            Identities.Add(message.MessageId);
            return Identities.Count == 1 ? first : adapter.PublishAsync(message, context, cancellationToken);
        }
    }
}

internal sealed record CompatibilityEvent(string Value, DateTimeOffset OccurredOn) : IDomainEvent;
[JsonSerializable(typeof(CompatibilityEvent))]
internal sealed partial class CompatibilityJsonContext : JsonSerializerContext;
