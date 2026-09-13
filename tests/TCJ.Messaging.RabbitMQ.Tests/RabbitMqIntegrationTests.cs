using System.Diagnostics;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using RabbitMQ.Client;
using TCJ.Core.DomainEvents;
using TCJ.Core.Inbox;
using TCJ.Core.Outbox;
using TCJ.Messaging.Configuration;
using TCJ.Messaging.ConformanceTests;
using TCJ.Messaging.Extensions;
using TCJ.Messaging.Envelopes;
using TCJ.Messaging.Integration;
using TCJ.Messaging.Publishing;
using TCJ.Messaging.Receiving;
using TCJ.Messaging.RabbitMQ.Tests.Infrastructure;
using TCJ.Messaging.RabbitMQ.Topology;

namespace TCJ.Messaging.RabbitMQ.Tests;

[Collection(RabbitMqIntegrationCollection.Name), Trait("Category", "RabbitMQIntegration")]
public sealed class RabbitMqIntegrationTests(RabbitMqContainerFixture fixture)
{
    private static CancellationTokenSource Deadline() => new(TimeSpan.FromSeconds(20));

    [Fact] public async Task Connect_successfully() { await using var h = await RabbitMqTestHarness.CreateAsync(fixture); Assert.True(await h.HealthProbe.IsReadyAsync()); }

    [Fact] public async Task Authentication_failure_is_sanitized()
    {
        const string marker = "tcj-compat-secret-auth";
        await using var h = await RabbitMqTestHarness.CreateAsync(fixture, password: marker, validateStartup: false);
        Exception error = await Assert.ThrowsAnyAsync<Exception>(() => h.Services.GetRequiredService<IMessagingStartupValidator>().ValidateAsync());
        Assert.DoesNotContain(marker, error.Message);
        Assert.False(await h.HealthProbe.IsReadyAsync());
    }

    [Fact] public async Task Topology_declaration_creates_queue() { await using var h = await RabbitMqTestHarness.CreateAsync(fixture); await using var a = await RabbitMqBrokerAdmin.CreateAsync(fixture); Assert.Equal(0u, await a.MessageCountAsync(h.Topology.Queue)); }

    [Fact] public async Task Topology_conflict_is_rejected()
    {
        var topology = RabbitMqTestTopology.Create();
        await using var a = await RabbitMqBrokerAdmin.CreateAsync(fixture);
        await a.Channel.ExchangeDeclareAsync(topology.Exchange, "fanout", true);
        await using var h = await RabbitMqTestHarness.CreateAsync(fixture, topology: topology, validateStartup: false);
        await Assert.ThrowsAnyAsync<Exception>(() => h.Services.GetRequiredService<IMessagingStartupValidator>().ValidateAsync());
    }

    [Fact] public async Task ValidateOnly_accepts_existing_topology() { await using var declared = await RabbitMqTestHarness.CreateAsync(fixture); await using var h = await RabbitMqTestHarness.CreateAsync(fixture, RabbitMqTopologyMode.ValidateOnly, topology: declared.Topology); Assert.True(await h.HealthProbe.IsReadyAsync()); }

    [Fact] public async Task Disabled_uses_external_topology() { await using var declared = await RabbitMqTestHarness.CreateAsync(fixture); await using var h = await RabbitMqTestHarness.CreateAsync(fixture, RabbitMqTopologyMode.Disabled, topology: declared.Topology); Assert.True((await h.PublishAsync()).IsSuccess); }

    [Fact] public async Task Publish_and_confirm_persists_message() { await using var h = await RabbitMqTestHarness.CreateAsync(fixture); Assert.True((await h.PublishAsync()).IsSuccess); await using var a = await RabbitMqBrokerAdmin.CreateAsync(fixture); Assert.NotNull(await a.GetAsync(h.Topology.Queue)); }

    [Fact] public async Task Unroutable_mandatory_publish_is_permanent() { await using var h = await RabbitMqTestHarness.CreateAsync(fixture); var result = await h.PublishAsync(RabbitMqTestHarness.CreateEnvelope(messageType: "unbound")); Assert.False(result.IsSuccess); Assert.Equal(MessagingFailureCategory.PermanentTopology, result.FailureCategory); }

    [Fact] public async Task Publish_nack_does_not_report_success()
    {
        await using var h = await RabbitMqTestHarness.CreateAsync(fixture);
        await using var a = await RabbitMqBrokerAdmin.CreateAsync(fixture);
        string queue = h.Topology.Queue + ".bounded";
        await a.Channel.QueueDeclareAsync(queue, true, false, false, new Dictionary<string, object?> { ["x-max-length"] = 1, ["x-overflow"] = "reject-publish" });
        await a.Channel.QueueBindAsync(queue, h.Topology.Exchange, h.Topology.RoutingKey);
        Assert.True((await h.PublishAsync()).IsSuccess);
        Assert.False((await h.PublishAsync()).IsSuccess);
    }

    [Fact] public async Task Consume_and_acknowledge_removes_delivery()
    {
        await using var h = await RabbitMqTestHarness.CreateAsync(fixture); await h.PublishAsync(); using var c = Deadline();
        await using (var lease = await h.ReceiveOneAsync(c.Token)) await lease.Message.Settlement.CompleteAsync(c.Token);
        await using var a = await RabbitMqBrokerAdmin.CreateAsync(fixture); Assert.Null(await a.GetAsync(h.Topology.Queue));
    }

    [Fact] public async Task Duplicate_redelivery_preserves_identity()
    {
        await using var h = await RabbitMqTestHarness.CreateAsync(fixture); await h.PublishAsync(RabbitMqTestHarness.CreateEnvelope("duplicate")); using var c = Deadline();
        await using (var first = await h.ReceiveOneAsync(c.Token)) await first.Message.Settlement.AbandonAsync(c.Token);
        await using var second = await h.ReceiveOneAsync(c.Token); Assert.Equal("duplicate", second.Message.Envelope.MessageId); await second.Message.Settlement.CompleteAsync(c.Token);
    }

    [Fact] public async Task Retry_routing_preserves_identity_and_attempt()
    {
        await using var h = await RabbitMqTestHarness.CreateAsync(fixture); await h.PublishAsync(RabbitMqTestHarness.CreateEnvelope("retry")); using var c = Deadline();
        await using (var first = await h.ReceiveOneAsync(c.Token)) await first.Message.Settlement.RetryAsync(new RetrySettlementOptions(), c.Token);
        await using var second = await h.ReceiveOneAsync(c.Token); Assert.Equal("retry", second.Message.Envelope.MessageId); Assert.True(second.Message.Delivery.DeliveryAttempt >= 2); await second.Message.Settlement.CompleteAsync(c.Token);
    }

    [Fact] public async Task Dead_letter_routing_preserves_identity()
    {
        await using var h = await RabbitMqTestHarness.CreateAsync(fixture); await h.PublishAsync(RabbitMqTestHarness.CreateEnvelope("dead")); using var c = Deadline();
        await using (var lease = await h.ReceiveOneAsync(c.Token)) await lease.Message.Settlement.DeadLetterAsync(new DeadLetterOptions { Reason = "invalid" }, c.Token);
        await using var a = await RabbitMqBrokerAdmin.CreateAsync(fixture); var dead = await a.GetAsync(h.Topology.DeadLetterQueue); Assert.NotNull(dead); Assert.Equal("dead", dead.BasicProperties.MessageId);
    }

    [Fact] public async Task Poison_message_reaches_terminal_queue()
    {
        await using var h = await RabbitMqTestHarness.CreateAsync(fixture, maximumAttempts: 1); await h.PublishAsync(); using var c = Deadline();
        await using (var lease = await h.ReceiveOneAsync(c.Token)) await lease.Message.Settlement.RetryAsync(new RetrySettlementOptions(), c.Token);
        await using var a = await RabbitMqBrokerAdmin.CreateAsync(fixture); Assert.NotNull(await a.GetAsync(h.Topology.DeadLetterQueue));
    }

    [Fact] public async Task Prefetch_enforcement_bounds_unacknowledged_deliveries()
    {
        await using var h = await RabbitMqTestHarness.CreateAsync(fixture, prefetch: 1, concurrency: 1);
        await h.PublishAsync(); await h.PublishAsync(); using var c = Deadline();
        await using var receiver = h.Receiver.ReceiveAsync(new ReceiveContext { Source = h.Topology.Queue }, c.Token).GetAsyncEnumerator(c.Token);
        Assert.True(await receiver.MoveNextAsync()); var first = receiver.Current;
        Task<bool> next = receiver.MoveNextAsync().AsTask();
        Assert.NotSame(next, await Task.WhenAny(next, Task.Delay(250, c.Token)));
        await first.Settlement.CompleteAsync(c.Token); Assert.True(await next); await receiver.Current.Settlement.CompleteAsync(c.Token);
    }

    [Fact] public async Task Maximum_concurrency_is_bounded_by_configuration()
    {
        await using var h = await RabbitMqTestHarness.CreateAsync(fixture, prefetch: 1, concurrency: 2, validateStartup: false);
        await Assert.ThrowsAnyAsync<ArgumentException>(() => h.Services.GetRequiredService<IMessagingStartupValidator>().ValidateAsync());
    }

    [Fact] public async Task Connection_loss_during_publish_does_not_false_confirm()
    {
        await using var h = await RabbitMqTestHarness.CreateAsync(fixture); await fixture.StopAsync();
        try { Assert.False((await h.PublishAsync()).IsSuccess); }
        finally { await fixture.EnsureRunningAsync(); }
    }

    [Fact] public async Task Connection_loss_during_consume_leaves_unsettled_message_recoverable()
    {
        await using var h = await RabbitMqTestHarness.CreateAsync(fixture); await h.PublishAsync(RabbitMqTestHarness.CreateEnvelope("recovery")); using var c = Deadline();
        await using (var lease = await h.ReceiveOneAsync(c.Token))
        {
            await fixture.StopAsync();
            try { await Assert.ThrowsAnyAsync<Exception>(() => lease.Message.Settlement.CompleteAsync(c.Token)); }
            finally { await fixture.EnsureRunningAsync(); }
        }
        await using var recovered = await RabbitMqTestHarness.CreateAsync(fixture, topology: h.Topology);
        using var recoveryDeadline = Deadline();
        await using var redelivery = await recovered.ReceiveOneAsync(recoveryDeadline.Token); Assert.Equal("recovery", redelivery.Message.Envelope.MessageId); await redelivery.Message.Settlement.CompleteAsync(recoveryDeadline.Token);
    }

    [Fact] public async Task Automatic_recovery_restores_publication()
    {
        await using var h = await RabbitMqTestHarness.CreateAsync(fixture);
        await fixture.StopAsync();
        try
        {
            Assert.False((await h.PublishAsync()).IsSuccess);
        }
        finally
        {
            await fixture.EnsureRunningAsync();
        }

        using var c = Deadline();
        while (!await h.HealthProbe.IsReadyAsync(c.Token))
        {
            await Task.Delay(100, c.Token);
        }

        Assert.True((await h.PublishAsync()).IsSuccess);
    }

    [Fact] public async Task Graceful_shutdown_cancels_idle_receive()
    {
        await using var h = await RabbitMqTestHarness.CreateAsync(fixture); using var c = Deadline();
        await using var receiver = h.Receiver.ReceiveAsync(new ReceiveContext { Source = h.Topology.Queue }, c.Token).GetAsyncEnumerator(c.Token);
        var pending = receiver.MoveNextAsync().AsTask(); c.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    [Fact] public async Task Header_filtering_uses_existing_Conformance() => await Scenarios().Forbidden_headers_are_removed_before_adapter_delivery();
    [Fact] public async Task Conformance_identity_and_metadata() => await Scenarios().Publish_and_receive_preserve_stable_message_identity_and_metadata();
    [Fact] public async Task Conformance_bounded_telemetry() => await Scenarios().Publish_emits_bounded_activity_and_metrics();

    [Fact] public async Task Trace_propagation_preserves_valid_context() => await CheckTraceAsync("00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01", true);
    [Fact] public async Task Malformed_trace_context_is_filtered() => await CheckTraceAsync("invalid", false);

    private async Task CheckTraceAsync(string trace, bool expected)
    {
        await using var h = await RabbitMqTestHarness.CreateAsync(fixture);
        Assert.True((await h.PublishAsync(RabbitMqTestHarness.CreateEnvelope(headers: new Dictionary<string, string> { ["traceparent"] = trace }))).IsSuccess);
        using var c = Deadline(); await using var lease = await h.ReceiveOneAsync(c.Token);
        Assert.Equal(expected, lease.Message.Envelope.Headers.ContainsKey("traceparent")); await lease.Message.Settlement.CompleteAsync(c.Token);
    }

    [Fact] public async Task Unsupported_defer_is_explicit()
    {
        await using var h = await RabbitMqTestHarness.CreateAsync(fixture); await h.PublishAsync(); using var c = Deadline(); await using var lease = await h.ReceiveOneAsync(c.Token);
        await Assert.ThrowsAsync<MessagingCapabilityException>(() => lease.Message.Settlement.DeferAsync(c.Token)); await lease.Message.Settlement.CompleteAsync(c.Token);
    }

    [Fact] public async Task Time_to_live_expires_before_delivery()
    {
        await using var h = await RabbitMqTestHarness.CreateAsync(fixture);
        Assert.True((await h.Publisher.PublishAsync(RabbitMqTestHarness.CreateEnvelope(), new PublishContext { Destination = h.Topology.Exchange, TimeToLive = TimeSpan.FromMilliseconds(100) })).IsSuccess);
        await Task.Delay(300, TestContext.Current.CancellationToken);
        await using var admin = await RabbitMqBrokerAdmin.CreateAsync(fixture);
        Assert.Null(await admin.GetAsync(h.Topology.Queue));
    }

    [Fact] public async Task Partitioning_uses_explicit_routing_strategy()
    {
        var topology = RabbitMqTestTopology.Create() with { RoutingKey = "tenant-a" };
        await using var h = await RabbitMqTestHarness.CreateAsync(fixture, topology: topology,
            configureServices: services => services.AddSingleton<IRabbitMqRoutingKeyStrategy>(new PartitionRouting()));
        await using var admin = await RabbitMqBrokerAdmin.CreateAsync(fixture);
        string otherQueue = topology.Queue + ".tenant-b";
        await admin.Channel.QueueDeclareAsync(otherQueue, true, false, false);
        await admin.Channel.QueueBindAsync(otherQueue, topology.Exchange, "tenant-b");
        // Each key selects its own queue through explicitly configured adapter routing.
        Assert.True((await h.Publisher.PublishAsync(RabbitMqTestHarness.CreateEnvelope(), new PublishContext { Destination = h.Topology.Exchange, PartitionKey = "tenant-a" })).IsSuccess);
        Assert.True((await h.Publisher.PublishAsync(RabbitMqTestHarness.CreateEnvelope("other-tenant"), new PublishContext { Destination = h.Topology.Exchange, PartitionKey = "tenant-b" })).IsSuccess);
        using var c = Deadline(); await using var lease = await h.ReceiveOneAsync(c.Token);
        Assert.Equal("tenant-a", lease.Message.Envelope.PartitionKey);
        await lease.Message.Settlement.CompleteAsync(c.Token);
        var other = await admin.GetAsync(otherQueue);
        Assert.NotNull(other); Assert.Equal("other-tenant", other.BasicProperties.MessageId);
        Assert.Null(await admin.GetAsync(topology.Queue));
    }

    [Fact] public async Task Inbox_settles_only_after_durable_outcome()
    {
        await using var h = await RabbitMqTestHarness.CreateAsync(fixture); await h.PublishAsync(); using var c = Deadline(); await using var lease = await h.ReceiveOneAsync(c.Token);
        var gate = new TaskCompletionSource<InboxHandlingResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var bridge = Bridge(h, new StubInboxPipeline((_, _) => gate.Task));
        var pending = bridge.ProcessAsync(lease.Message, c.Token); Assert.False(pending.IsCompleted);
        gate.SetResult(new InboxHandlingResult(InboxHandlingOutcome.Acknowledge)); Assert.Equal(MessageSettlement.Complete, (await pending).Settlement);
    }

    [Fact] public async Task Transaction_failure_does_not_acknowledge_Inbox()
    {
        await using var h = await RabbitMqTestHarness.CreateAsync(fixture); await h.PublishAsync(); using var c = Deadline(); await using var lease = await h.ReceiveOneAsync(c.Token);
        var bridge = Bridge(h, new StubInboxPipeline((_, _) => throw new InvalidOperationException("transaction failed")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => bridge.ProcessAsync(lease.Message, c.Token));
        await lease.Message.Settlement.AbandonAsync(c.Token);
    }

    [Fact] public async Task Outbox_bridge_preserves_durable_identity()
    {
        Guid id = Guid.NewGuid();
        await using var h = await RabbitMqTestHarness.CreateAsync(fixture, configureServices: services =>
        {
            services.AddSingleton<IDomainEventDispatcher, NoopDispatcher>();
            services.AddSingleton<IOutboxMessageContextAccessor>(new StubOutboxContextAccessor { Current = new OutboxMessageContext(id, "test.message.v1", 1, "corr", "cause", null) });
            services.AddTcjMessage("test.message", 1, RabbitMqTestJsonContext.Default.RabbitMqOutboxEvent);
            services.AddTcjMessagingOutboxBridge();
        });
        await h.Services.GetRequiredService<IDomainEventDispatcher>().DispatchAsync([new RabbitMqOutboxEvent("ok", DateTimeOffset.UtcNow)]);
        using var c = Deadline(); await using var lease = await h.ReceiveOneAsync(c.Token); Assert.Equal(id.ToString("D"), lease.Message.Envelope.MessageId); await lease.Message.Settlement.CompleteAsync(c.Token);
    }

    private static InboxTransportBridge Bridge(RabbitMqTestHarness h, IInboxPipeline pipeline) => new(pipeline, new TcjInboxOptions { ConsumerName = "rabbit-test" }, h.Services.GetRequiredService<TcjMessagingOptions>(), h.Descriptor, h.Services.GetRequiredService<MessagingHeaderPolicy>(), TimeProvider.System);

    private MessagingAdapterConformanceTests Scenarios() => ReusableMessagingScenarios.Create(async () =>
    {
        var topology = RabbitMqTestTopology.Create() with { RoutingKey = "conformance.message.v1" };
        var h = await RabbitMqTestHarness.CreateAsync(fixture, topology: topology);
        return new MessagingAdapterHarness(h.Publisher, h.Services.GetRequiredService<IMessageBatchPublisher>(), h.Receiver, h.Descriptor, h.HealthProbe, TimeProvider.System, h.Topology.Queue, h, h.Topology.Exchange, scenarioTimeout: TimeSpan.FromSeconds(25));
    });

    private sealed class NoopDispatcher : IDomainEventDispatcher { public Task DispatchAsync(IReadOnlyCollection<IDomainEvent> events, CancellationToken cancellationToken = default) => Task.CompletedTask; }
    private sealed class PartitionRouting : IRabbitMqRoutingKeyStrategy
    {
        public string GetRoutingKey(string messageType, int messageVersion, TransportMessageEnvelope envelope) =>
            envelope.PartitionKey ?? throw new InvalidOperationException("This test requires an explicit partition key.");
    }
}

internal sealed record RabbitMqOutboxEvent(string Value, DateTimeOffset OccurredOn) : IDomainEvent;
[JsonSerializable(typeof(RabbitMqOutboxEvent))]
internal partial class RabbitMqTestJsonContext : JsonSerializerContext;
