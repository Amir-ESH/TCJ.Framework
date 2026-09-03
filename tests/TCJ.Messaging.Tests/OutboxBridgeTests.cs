using Microsoft.Extensions.DependencyInjection;
using TCJ.Core.DomainEvents;
using TCJ.Core.Outbox;
using TCJ.Core.Resilience;
using TCJ.Messaging.Extensions;
using TCJ.Messaging.InMemory;
using TCJ.Messaging.Publishing;
using TCJ.Messaging.Receiving;

namespace TCJ.Messaging.Tests;

public sealed class OutboxBridgeTests
{
    [Fact]
    public async Task MessagingOutbox_success_preserves_outbox_identity_and_correlation()
    {
        Guid messageId = Guid.Parse("11111111-2222-3333-4444-555555555555");
        StubDispatcher inner = new();
        StubOutboxContextAccessor accessor = new()
        {
            Current = new OutboxMessageContext(messageId, "test.event.v1", 1, "correlation-1", "causation-1", "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01", "vendor=value")
        };

        using ServiceProvider provider = CreateProvider(inner, accessor);
        IDomainEventDispatcher dispatcher = provider.GetRequiredService<IDomainEventDispatcher>();

        await dispatcher.DispatchAsync([
            new TestDomainEvent("created", DateTimeOffset.Parse("2026-09-02T00:00:00Z"))
        ]);

        Assert.Equal(0, inner.Calls);
        IMessageReceiver receiver = provider.GetRequiredService<IMessageReceiver>();
        await using IAsyncEnumerator<ReceivedMessage> enumerator = receiver
            .ReceiveAsync(new ReceiveContext { Source = "test.event.v1" })
            .GetAsyncEnumerator();
        Assert.True(await enumerator.MoveNextAsync());
        Assert.Equal(messageId.ToString("D"), enumerator.Current.Envelope.MessageId);
        Assert.Equal("test.event", enumerator.Current.Envelope.MessageType);
        Assert.Equal(1, enumerator.Current.Envelope.MessageVersion);
        Assert.Equal("correlation-1", enumerator.Current.Envelope.CorrelationId);
        Assert.Equal("causation-1", enumerator.Current.Envelope.CausationId);
        Assert.Equal("00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01", enumerator.Current.Envelope.Headers["traceparent"]);
        Assert.Equal("vendor=value", enumerator.Current.Envelope.Headers["tracestate"]);
        await enumerator.Current.Settlement.CompleteAsync();
    }

    [Fact]
    public async Task MessagingOutbox_transient_publish_failure_is_retryable_by_core_classifier()
    {
        StubDispatcher inner = new();
        StubOutboxContextAccessor accessor = CreateOutboxContext();
        using ServiceProvider provider = CreateProvider(inner, accessor);
        provider.GetRequiredService<InMemoryMessagingTransport>().EnqueuePublishResult(
            new PublishResult(
                PublishOutcome.TransientFailure,
                FailureCategory: MessagingFailureCategory.TransientConnection,
                FailureType: "TransientConnection"));

        Exception failure = await Assert.ThrowsAnyAsync<Exception>(() =>
            provider.GetRequiredService<IDomainEventDispatcher>().DispatchAsync([
                new TestDomainEvent("created", DateTimeOffset.Parse("2026-09-02T00:00:00Z"))
            ]));

        ITransientFailureClassifier classifier = Assert.Single(provider.GetServices<ITransientFailureClassifier>());
        Assert.True(classifier.IsTransient(failure));
        Assert.Equal(0, inner.Calls);
    }

    [Fact]
    public async Task MessagingOutbox_permanent_publish_failure_is_not_retryable()
    {
        StubDispatcher inner = new();
        StubOutboxContextAccessor accessor = CreateOutboxContext();
        using ServiceProvider provider = CreateProvider(inner, accessor);
        provider.GetRequiredService<InMemoryMessagingTransport>().EnqueuePublishResult(
            new PublishResult(
                PublishOutcome.PermanentFailure,
                FailureCategory: MessagingFailureCategory.PermanentTopology,
                FailureType: "PermanentTopology"));

        Exception failure = await Assert.ThrowsAnyAsync<Exception>(() =>
            provider.GetRequiredService<IDomainEventDispatcher>().DispatchAsync([
                new TestDomainEvent("created", DateTimeOffset.Parse("2026-09-02T00:00:00Z"))
            ]));

        ITransientFailureClassifier classifier = Assert.Single(provider.GetServices<ITransientFailureClassifier>());
        Assert.False(classifier.IsTransient(failure));
        Assert.Equal(0, inner.Calls);
    }

    [Fact]
    public async Task MessagingOutbox_bridge_is_inert_outside_outbox_delivery()
    {
        StubDispatcher inner = new();
        StubOutboxContextAccessor accessor = new();
        using ServiceProvider provider = CreateProvider(inner, accessor);

        await provider.GetRequiredService<IDomainEventDispatcher>().DispatchAsync([
            new TestDomainEvent("local", DateTimeOffset.Parse("2026-09-02T00:00:00Z"))
        ]);

        Assert.Equal(1, inner.Calls);
    }

    private static StubOutboxContextAccessor CreateOutboxContext() => new()
    {
        Current = new OutboxMessageContext(
            Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
            "test.event.v1",
            1,
            "correlation-1",
            "causation-1")
    };

    private static ServiceProvider CreateProvider(StubDispatcher inner, StubOutboxContextAccessor accessor)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IDomainEventDispatcher>(inner);
        services.AddSingleton<IOutboxMessageContextAccessor>(accessor);
        services.AddTcjMessaging();
        services.AddTcjMessage("test.event", 1, TestJsonContext.Default.TestDomainEvent);
        services.AddTcjInMemoryMessaging();
        services.AddTcjMessagingOutboxBridge();
        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });
    }
}
