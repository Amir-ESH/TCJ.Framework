using Azure.Messaging.ServiceBus;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using TCJ.Core.DomainEvents;
using TCJ.Core.Inbox;
using TCJ.Core.Outbox;
using TCJ.Messaging.AzureServiceBus.Configuration;
using TCJ.Messaging.AzureServiceBus.Extensions;
using TCJ.Messaging.AzureServiceBus.Tests.Infrastructure;
using TCJ.Messaging.Configuration;
using TCJ.Messaging.Extensions;
using TCJ.Messaging.Integration;
using TCJ.Messaging.Publishing;
using TCJ.Messaging.Receiving;

namespace TCJ.Messaging.AzureServiceBus.Tests;

public sealed class AzureServiceBusInboxOutboxIntegrationTests
{
    [Fact, Trait("Category", "AzureServiceBusIntegration")]
    public async Task Inbox_acknowledge_completes_only_after_pipeline_returns_committed_outcome()
    {
        await using AzureServiceBusIntegrationEnvironment? env = await AzureServiceBusIntegrationEnvironment.CreateAsync();
        if (env is null) return;

        string queue = await env.CreateQueueAsync();
        await using ServiceProvider provider = CreateTransportProvider(env, queue);
        await provider.GetRequiredService<IMessagePublisher>().PublishAsync(
            Envelope("inbox-commit"), new PublishContext { Destination = queue });

        await using AzureServiceBusReceivedLease lease = await ReceiveOne(provider, queue);
        ReceivedMessage received = lease.Message;
        var gate = new TaskCompletionSource<InboxHandlingResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pipeline = new StubInboxPipeline((_, _) => gate.Task);
        InboxTransportBridge bridge = CreateInboxBridge(provider, pipeline);

        Task<InboxTransportBridgeResult> processing = bridge.ProcessAsync(received);
        Assert.False(processing.IsCompleted);

        gate.SetResult(new InboxHandlingResult(InboxHandlingOutcome.Acknowledge));
        InboxTransportBridgeResult result = await processing;
        Assert.Equal(MessageSettlement.Complete, result.Settlement);

        await using var direct = env.Client.CreateReceiver(queue);
        Assert.Null(await direct.ReceiveMessageAsync(TimeSpan.FromSeconds(1)));
    }

    [Fact, Trait("Category", "AzureServiceBusIntegration")]
    public async Task Inbox_duplicate_redelivery_maps_to_complete_without_second_business_effect()
    {
        await using AzureServiceBusIntegrationEnvironment? env = await AzureServiceBusIntegrationEnvironment.CreateAsync();
        if (env is null) return;

        string queue = await env.CreateQueueAsync();
        await using ServiceProvider provider = CreateTransportProvider(env, queue);
        await provider.GetRequiredService<IMessagePublisher>().PublishAsync(
            Envelope("inbox-duplicate"), new PublishContext { Destination = queue });

        int pipelineCalls = 0;
        var pipeline = new StubInboxPipeline((_, _) =>
        {
            pipelineCalls++;
            return Task.FromResult(new InboxHandlingResult(InboxHandlingOutcome.IgnoreDuplicate, IsDuplicate: true));
        });

        await using AzureServiceBusReceivedLease lease = await ReceiveOne(provider, queue);
        InboxTransportBridgeResult result = await CreateInboxBridge(provider, pipeline)
            .ProcessAsync(lease.Message);

        Assert.Equal(1, pipelineCalls);
        Assert.True(result.InboxResult.IsDuplicate);
        Assert.Equal(MessageSettlement.Complete, result.Settlement);
    }

    [Fact, Trait("Category", "AzureServiceBusIntegration")]
    public async Task Inbox_retry_uses_configured_scheduled_retry_without_completing_before_schedule_succeeds()
    {
        await using AzureServiceBusIntegrationEnvironment? env = await AzureServiceBusIntegrationEnvironment.CreateAsync();
        if (env is null) return;

        string queue = await env.CreateQueueAsync();
        await using ServiceProvider provider = CreateTransportProvider(env, queue);
        await provider.GetRequiredService<IMessagePublisher>().PublishAsync(
            Envelope("inbox-retry"), new PublishContext { Destination = queue });

        var pipeline = new StubInboxPipeline((_, _) => Task.FromResult(
            new InboxHandlingResult(InboxHandlingOutcome.Retry, 2, InboxFailureType.TransientInfrastructure)));
        InboxTransportBridgeResult result;
        await using (AzureServiceBusReceivedLease lease = await ReceiveOne(provider, queue))
        {
            result = await CreateInboxBridge(provider, pipeline)
                .ProcessAsync(lease.Message);
        }

        Assert.Equal(MessageSettlement.Retry, result.Settlement);
        await using var direct = env.Client.CreateReceiver(queue);
        ServiceBusReceivedMessage? retry = await direct.ReceiveMessageAsync(TimeSpan.FromSeconds(10));
        Assert.NotNull(retry);
        Assert.Equal("inbox-retry", retry.ApplicationProperties["tcj-message-id"]?.ToString());
        Assert.StartsWith("inbox-retry:retry:", retry.MessageId, StringComparison.Ordinal);
        await direct.CompleteMessageAsync(retry);
    }

    [Fact, Trait("Category", "AzureServiceBusIntegration")]
    public async Task Inbox_permanent_failure_dead_letters_after_pipeline_outcome()
    {
        await using AzureServiceBusIntegrationEnvironment? env = await AzureServiceBusIntegrationEnvironment.CreateAsync();
        if (env is null) return;

        string queue = await env.CreateQueueAsync();
        await using ServiceProvider provider = CreateTransportProvider(env, queue);
        await provider.GetRequiredService<IMessagePublisher>().PublishAsync(
            Envelope("inbox-dead-letter"), new PublishContext { Destination = queue });

        var pipeline = new StubInboxPipeline((_, _) => Task.FromResult(
            new InboxHandlingResult(InboxHandlingOutcome.DeadLetter, 1, InboxFailureType.PermanentValidation)));
        InboxTransportBridgeResult result;
        await using (AzureServiceBusReceivedLease lease = await ReceiveOne(provider, queue))
        {
            result = await CreateInboxBridge(provider, pipeline)
                .ProcessAsync(lease.Message);
        }
        Assert.Equal(MessageSettlement.DeadLetter, result.Settlement);

        await using var dlq = env.Client.CreateReceiver(queue, new ServiceBusReceiverOptions { SubQueue = SubQueue.DeadLetter });
        ServiceBusReceivedMessage? dead = await dlq.ReceiveMessageAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(dead);
        Assert.Equal("inbox-dead-letter", dead.MessageId);
        await dlq.CompleteMessageAsync(dead);
    }

    [Fact, Trait("Category", "AzureServiceBusIntegration")]
    public async Task Outbox_bridge_publishes_stable_context_identity_and_metadata_through_azure_service_bus()
    {
        await using AzureServiceBusIntegrationEnvironment? env = await AzureServiceBusIntegrationEnvironment.CreateAsync();
        if (env is null) return;

        string messageType = $"tcj-s48-outbox-{Guid.NewGuid():N}";
        string queue = await env.CreateQueueAsync($"{messageType}.v1");
        Guid logicalId = Guid.Parse("a13ae848-e5f8-4ac3-b6f5-3bc4d3ca21c1");
        var inner = new StubDispatcher();
        var accessor = new StubOutboxContextAccessor
        {
            Current = new OutboxMessageContext(
                logicalId,
                $"{messageType}.v1",
                1,
                "corr-outbox",
                "cause-outbox",
                "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01")
        };

        var services = new ServiceCollection();
        services.AddSingleton<IDomainEventDispatcher>(inner);
        services.AddSingleton<IOutboxMessageContextAccessor>(accessor);
        services.AddTcjMessaging();
        services.AddTcjMessage(messageType, 1, AzureServiceBusTestJsonContext.Default.AzureOutboxEvent);
        services.AddTcjAzureServiceBus(env.ConnectionString, options => options.ReadinessDestination = queue);
        services.AddTcjMessagingOutboxBridge();
        await using ServiceProvider provider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

        await provider.GetRequiredService<IDomainEventDispatcher>().DispatchAsync([
            new AzureOutboxEvent("created", DateTimeOffset.UtcNow)
        ]);

        Assert.Equal(0, inner.Calls);
        await using ServiceBusReceiver receiver = env.Client.CreateReceiver(queue);
        ServiceBusReceivedMessage? message = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(message);
        Assert.Equal(logicalId.ToString("D"), message.MessageId);
        Assert.Equal(messageType, message.Subject);
        Assert.Equal("corr-outbox", message.CorrelationId);
        Assert.Equal("cause-outbox", message.ApplicationProperties["tcj-causation-id"]?.ToString());
        await receiver.CompleteMessageAsync(message);
    }

    private static ServiceProvider CreateTransportProvider(AzureServiceBusIntegrationEnvironment env, string queue)
    {
        var services = new ServiceCollection();
        services.AddTcjMessaging();
        services.AddTcjAzureServiceBus(env.ConnectionString, options =>
        {
            options.ManagementConnectionString = env.ManagementConnectionString;
            options.ReadinessDestination = queue;
            options.DefaultRetryDelay = TimeSpan.FromSeconds(1);
            options.RetrySettlementStrategy = AzureServiceBusRetrySettlementStrategy.ScheduledClone;
        });
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    private static InboxTransportBridge CreateInboxBridge(ServiceProvider provider, IInboxPipeline pipeline)
    {
        var messagingOptions = provider.GetRequiredService<TcjMessagingOptions>();
        return new InboxTransportBridge(
            pipeline,
            new TcjInboxOptions { ConsumerName = "azure-service-bus-integration" },
            messagingOptions,
            provider.GetRequiredService<MessagingTransportDescriptor>(),
            provider.GetRequiredService<MessagingHeaderPolicy>(),
            TimeProvider.System);
    }

    private static async Task<AzureServiceBusReceivedLease> ReceiveOne(ServiceProvider provider, string source)
    {
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        IAsyncEnumerator<ReceivedMessage> enumerator = provider
            .GetRequiredService<IMessageReceiver>()
            .ReceiveAsync(new ReceiveContext { Source = source }, cts.Token)
            .GetAsyncEnumerator(cts.Token);
        try
        {
            if (!await enumerator.MoveNextAsync())
                throw new InvalidOperationException("Expected Azure Service Bus delivery.");
            return new AzureServiceBusReceivedLease(enumerator, enumerator.Current, cts);
        }
        catch
        {
            cts.Cancel();
            await enumerator.DisposeAsync();
            cts.Dispose();
            throw;
        }
    }

    private static TCJ.Messaging.Envelopes.TransportMessageEnvelope Envelope(string id) =>
        new(id, "tcj.test", 1, "{}"u8.ToArray(), "application/json", DateTimeOffset.UtcNow, "corr", "cause");

    private sealed class StubInboxPipeline : IInboxPipeline
    {
        private readonly Func<IncomingMessageEnvelope, CancellationToken, Task<InboxHandlingResult>> _handler;
        public StubInboxPipeline(Func<IncomingMessageEnvelope, CancellationToken, Task<InboxHandlingResult>> handler) => _handler = handler;
        public Task<InboxHandlingResult> ProcessAsync(IncomingMessageEnvelope envelope, CancellationToken cancellationToken = default) =>
            _handler(envelope, cancellationToken);
    }

    private sealed class StubDispatcher : IDomainEventDispatcher
    {
        public int Calls { get; private set; }
        public Task DispatchAsync(IReadOnlyCollection<IDomainEvent> domainEvents, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            return Task.CompletedTask;
        }
    }

    private sealed class StubOutboxContextAccessor : IOutboxMessageContextAccessor
    {
        public OutboxMessageContext? Current { get; set; }
    }
}

internal sealed record AzureOutboxEvent(string Value, DateTimeOffset OccurredOn) : IDomainEvent;

[JsonSerializable(typeof(AzureOutboxEvent))]
internal sealed partial class AzureServiceBusTestJsonContext : JsonSerializerContext;
