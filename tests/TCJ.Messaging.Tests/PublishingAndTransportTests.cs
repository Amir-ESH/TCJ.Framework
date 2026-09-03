using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TCJ.Core.Inbox;
using TCJ.Messaging.Configuration;
using TCJ.Messaging.Envelopes;
using TCJ.Messaging.Extensions;
using TCJ.Messaging.InMemory;
using TCJ.Messaging.Publishing;
using TCJ.Messaging.Receiving;
using TCJ.Messaging.Topology;

namespace TCJ.Messaging.Tests;

public sealed class PublishingAndTransportTests
{
    [Fact]
    public async Task Publisher_resolves_default_destination()
    {
        using ServiceProvider provider = TestServices.Create();
        IMessagePublisher publisher = provider.GetRequiredService<IMessagePublisher>();
        PublishResult result = await publisher.PublishAsync(TestServices.Envelope(), new PublishContext());
        Assert.True(result.IsSuccess);
        InMemoryMessagingTransport transport = provider.GetRequiredService<InMemoryMessagingTransport>();
        await using IAsyncEnumerator<ReceivedMessage> receiver = transport.ReceiveAsync(new ReceiveContext { Source = "test.message.v1" }).GetAsyncEnumerator();
        Assert.True(await receiver.MoveNextAsync());
        Assert.Equal("message-1", receiver.Current.Envelope.MessageId);
    }

    [Fact]
    public async Task Publisher_reports_unsupported_partitioning()
    {
        using ServiceProvider provider = TestServices.Create();
        IMessagePublisher publisher = provider.GetRequiredService<IMessagePublisher>();
        PublishResult result = await publisher.PublishAsync(TestServices.Envelope(), new PublishContext { PartitionKey = "p1" });
        Assert.Equal(PublishOutcome.UnsupportedCapability, result.Outcome);
    }

    [Fact]
    public async Task Publisher_reports_unsupported_scheduling()
    {
        using ServiceProvider provider = TestServices.Create();
        IMessagePublisher publisher = provider.GetRequiredService<IMessagePublisher>();
        PublishResult result = await publisher.PublishAsync(TestServices.Envelope(), new PublishContext { ScheduledAtUtc = DateTimeOffset.UtcNow.AddMinutes(1) });
        Assert.Equal(PublishOutcome.UnsupportedCapability, result.Outcome);
    }

    [Fact]
    public async Task Publisher_enforces_payload_limit_before_adapter()
    {
        using ServiceProvider provider = TestServices.Create(options => options.MaximumPayloadBytes = 128);
        IMessagePublisher publisher = provider.GetRequiredService<IMessagePublisher>();
        var message = new TransportMessageEnvelope("id", "test.message", 1, new byte[129], "application/json", DateTimeOffset.UtcNow);
        PublishResult result = await publisher.PublishAsync(message, new PublishContext());
        Assert.Equal(MessagingFailureCategory.PayloadTooLarge, result.FailureCategory);
    }

    [Fact]
    public async Task Publisher_filters_sensitive_headers()
    {
        using ServiceProvider provider = TestServices.Create();
        IMessagePublisher publisher = provider.GetRequiredService<IMessagePublisher>();
        InMemoryMessagingTransport transport = provider.GetRequiredService<InMemoryMessagingTransport>();
        var message = TestServices.Envelope(headers: new Dictionary<string, string> { ["authorization"] = "Bearer secret" });
        PublishResult result = await publisher.PublishAsync(message, new PublishContext());
        Assert.True(result.IsSuccess);
        await using IAsyncEnumerator<ReceivedMessage> receiver = transport.ReceiveAsync(new ReceiveContext { Source = "test.message.v1" }).GetAsyncEnumerator();
        Assert.True(await receiver.MoveNextAsync());
        Assert.False(receiver.Current.Envelope.Headers.ContainsKey("authorization"));
    }

    [Fact]
    public async Task Batch_results_remain_index_aligned_on_partial_failure()
    {
        using ServiceProvider provider = TestServices.Create();
        InMemoryMessagingTransport transport = provider.GetRequiredService<InMemoryMessagingTransport>();
        transport.EnqueuePublishResult(PublishResult.Published("a"));
        transport.EnqueuePublishResult(new PublishResult(PublishOutcome.TransientFailure, FailureCategory: MessagingFailureCategory.TransientConnection, FailureType: "TransientConnection"));
        IMessageBatchPublisher publisher = provider.GetRequiredService<IMessageBatchPublisher>();
        IReadOnlyList<PublishResult> results = await publisher.PublishBatchAsync(
            [TestServices.Envelope("1"), TestServices.Envelope("2")], new PublishContext());
        Assert.True(results[0].IsSuccess);
        Assert.Equal(PublishOutcome.TransientFailure, results[1].Outcome);
    }

    [Fact]
    public async Task Batch_rejects_mixed_destinations_without_reordering_results()
    {
        using ServiceProvider provider = TestServices.Create();
        IMessageBatchPublisher publisher = provider.GetRequiredService<IMessageBatchPublisher>();
        var first = TestServices.Envelope("1", "test.message", 1);
        var second = TestServices.Envelope("2", "other.message", 1);
        IReadOnlyList<PublishResult> results = await publisher.PublishBatchAsync([first, second], new PublishContext());
        Assert.Equal(2, results.Count);
        Assert.Equal(PublishOutcome.UnsupportedCapability, results[1].Outcome);
    }

    [Fact]
    public async Task In_memory_transport_isolates_sources()
    {
        var options = new TcjMessagingOptions { MaximumBufferedMessages = 4 };
        var transport = new InMemoryMessagingTransport(options, TimeProvider.System);
        await transport.PublishAsync(TestServices.Envelope("b"), new PublishContext { Destination = "billing" });
        await transport.PublishAsync(TestServices.Envelope("o"), new PublishContext { Destination = "orders" });
        await using IAsyncEnumerator<ReceivedMessage> receiver = transport.ReceiveAsync(new ReceiveContext { Source = "orders" }).GetAsyncEnumerator();
        Assert.True(await receiver.MoveNextAsync());
        Assert.Equal("o", receiver.Current.Envelope.MessageId);
    }

    [Fact]
    public async Task In_memory_transport_preserves_message_id_on_redelivery()
    {
        var transport = new InMemoryMessagingTransport(new TcjMessagingOptions(), TimeProvider.System);
        await transport.PublishAsync(TestServices.Envelope("stable"), new PublishContext { Destination = "orders" });
        await using IAsyncEnumerator<ReceivedMessage> receiver = transport.ReceiveAsync(new ReceiveContext { Source = "orders" }).GetAsyncEnumerator();
        Assert.True(await receiver.MoveNextAsync());
        ReceivedMessage first = receiver.Current;
        await first.Settlement.RetryAsync(new RetrySettlementOptions());
        Assert.True(await receiver.MoveNextAsync());
        Assert.Equal("stable", receiver.Current.Envelope.MessageId);
        Assert.Equal(2, receiver.Current.Delivery.DeliveryAttempt);
    }

    [Fact]
    public async Task In_memory_transport_rejects_double_settlement()
    {
        var transport = new InMemoryMessagingTransport(new TcjMessagingOptions(), TimeProvider.System);
        await transport.PublishAsync(TestServices.Envelope(), new PublishContext { Destination = "orders" });
        await using IAsyncEnumerator<ReceivedMessage> receiver = transport.ReceiveAsync(new ReceiveContext { Source = "orders" }).GetAsyncEnumerator();
        Assert.True(await receiver.MoveNextAsync());
        await receiver.Current.Settlement.CompleteAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => receiver.Current.Settlement.CompleteAsync());
    }

    [Fact]
    public async Task In_memory_transport_dead_letter_metadata_is_sanitized()
    {
        var transport = new InMemoryMessagingTransport(new TcjMessagingOptions(), TimeProvider.System);
        await transport.PublishAsync(TestServices.Envelope(), new PublishContext { Destination = "orders" });
        await using IAsyncEnumerator<ReceivedMessage> receiver = transport.ReceiveAsync(new ReceiveContext { Source = "orders" }).GetAsyncEnumerator();
        Assert.True(await receiver.MoveNextAsync());
        await receiver.Current.Settlement.DeadLetterAsync(new DeadLetterOptions { Reason = new string('x', 200), Description = "safe\nvalue" });
        InMemoryDeadLetter dead = Assert.Single(transport.DeadLetters);
        Assert.True(dead.Options.Reason!.Length <= 128);
        Assert.DoesNotContain('\n', dead.Options.Description!);
    }

    [Fact]
    public async Task In_memory_transport_reports_unavailable_as_transient()
    {
        var transport = new InMemoryMessagingTransport(new TcjMessagingOptions(), TimeProvider.System) { IsAvailable = false };
        PublishResult result = await transport.PublishAsync(TestServices.Envelope(), new PublishContext { Destination = "orders" });
        Assert.True(result.IsRetryable);
    }

    [Fact]
    public void Topology_is_deterministic_and_versioned()
    {
        var topology = new DefaultMessageTopologyNamingStrategy(new TcjMessagingOptions { EnvironmentPrefix = "prod-" });
        Assert.Equal("prod-order.completed.v2", topology.GetDestination("order.completed", 2));
        Assert.Equal("prod-worker_a", topology.GetSubscription("worker_a"));
    }

    [Fact]
    public async Task Publisher_timeout_uses_injected_time_provider()
    {
        var fakeTime = new FakeTimeProvider(DateTimeOffset.Parse("2026-09-02T00:00:00Z"));
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(fakeTime);
        services.AddTcjMessaging(options => options.PublishTimeout = TimeSpan.FromSeconds(5));
        services.AddTcjMessage("test.message", 1, TestJsonContext.Default.TestMessage);
        services.AddTcjInMemoryMessaging();
        using ServiceProvider provider = services.BuildServiceProvider();
        InMemoryMessagingTransport transport = provider.GetRequiredService<InMemoryMessagingTransport>();
        transport.PublishDelay = TimeSpan.FromMinutes(1);
        Task<PublishResult> publish = provider.GetRequiredService<IMessagePublisher>().PublishAsync(TestServices.Envelope(), new PublishContext());
        fakeTime.Advance(TimeSpan.FromSeconds(6));
        PublishResult result = await publish;
        Assert.Equal(PublishOutcome.TimedOut, result.Outcome);
    }

    [Fact]
    public async Task In_memory_transport_applies_bounded_backpressure()
    {
        var transport = new InMemoryMessagingTransport(
            new TcjMessagingOptions { MaximumBufferedMessages = 1 },
            TimeProvider.System);

        await transport.PublishAsync(TestServices.Envelope("first"), new PublishContext { Destination = "orders" });
        Task<PublishResult> blockedPublish = transport.PublishAsync(
            TestServices.Envelope("second"),
            new PublishContext { Destination = "orders" });

        await Task.Yield();
        Assert.False(blockedPublish.IsCompleted);

        await using IAsyncEnumerator<ReceivedMessage> receiver = transport
            .ReceiveAsync(new ReceiveContext { Source = "orders" })
            .GetAsyncEnumerator();
        Assert.True(await receiver.MoveNextAsync());
        Assert.Equal("first", receiver.Current.Envelope.MessageId);

        PublishResult secondResult = await blockedPublish;
        Assert.True(secondResult.IsSuccess);
    }

    [Fact]
    public async Task Consumer_runner_allows_active_message_to_finish_during_graceful_shutdown()
    {
        var options = new TcjMessagingOptions
        {
            MaximumConcurrentMessages = 1,
            MaximumBufferedMessages = 4,
            ShutdownTimeout = TimeSpan.FromSeconds(30)
        };
        var descriptor = new MessagingTransportDescriptor
        {
            Name = "in-memory",
            Version = "1",
            Capabilities = new MessagingTransportCapabilities
            {
                SupportsDeadLetter = true,
                SupportsPeekLock = true,
                MaximumPayloadBytes = 1024 * 1024,
                MaximumHeaderBytes = 16 * 1024
            }
        };
        var transport = new InMemoryMessagingTransport(options, TimeProvider.System);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<InboxHandlingResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var inbox = new StubInboxPipeline(async (_, token) =>
        {
            entered.TrySetResult();
            return await release.Task.WaitAsync(token);
        });
        var inboxOptions = new TCJ.Core.Inbox.TcjInboxOptions { ConsumerName = "orders-worker" };
        var bridge = new TCJ.Messaging.Integration.InboxTransportBridge(
            inbox,
            inboxOptions,
            options,
            descriptor,
            new MessagingHeaderPolicy(options),
            TimeProvider.System);
        var state = new MessagingConsumerState();
        var runner = new MessageConsumerRunner(
            transport,
            bridge,
            options,
            new StubMessagingStartupValidator(),
            state,
            descriptor,
            TimeProvider.System);

        await transport.PublishAsync(TestServices.Envelope(), new PublishContext { Destination = "orders" });
        using var stop = new CancellationTokenSource();
        Task run = runner.RunAsync(new ReceiveContext { Source = "orders" }, stop.Token);
        await entered.Task;

        stop.Cancel();
        release.TrySetResult(new TCJ.Core.Inbox.InboxHandlingResult(
            TCJ.Core.Inbox.InboxHandlingOutcome.Acknowledge,
            1));

        await run;
        Assert.False(state.IsRunning);
        Assert.Equal(0, state.ActiveMessages);
    }

}
