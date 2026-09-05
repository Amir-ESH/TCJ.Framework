using System.Text;
using Microsoft.Extensions.DependencyInjection;
using RabbitMQ.Client;
using TCJ.Core.Inbox;
using TCJ.Core.Outbox;
using TCJ.Messaging.Configuration;
using TCJ.Messaging.Envelopes;
using TCJ.Messaging.Extensions;
using TCJ.Messaging.HealthChecks;
using TCJ.Messaging.Publishing;
using TCJ.Messaging.Receiving;
using TCJ.Messaging.RabbitMQ.Configuration;
using TCJ.Messaging.RabbitMQ.Extensions;
using TCJ.Messaging.RabbitMQ.Topology;

namespace TCJ.Messaging.RabbitMQ.Tests.Infrastructure;

internal sealed class RabbitMqTestHarness : IAsyncDisposable
{
    private RabbitMqTestHarness(ServiceProvider services, RabbitMqTestTopology topology)
    {
        Services = services;
        Topology = topology;
    }

    internal ServiceProvider Services { get; }
    internal RabbitMqTestTopology Topology { get; }
    internal IMessagePublisher Publisher => Services.GetRequiredService<IMessagePublisher>();
    internal IMessageReceiver Receiver => Services.GetRequiredService<IMessageReceiver>();
    internal MessagingTransportDescriptor Descriptor => Services.GetRequiredService<MessagingTransportDescriptor>();
    internal IMessagingTransportHealthProbe HealthProbe => Services.GetRequiredService<IMessagingTransportHealthProbe>();

    internal static async Task<RabbitMqTestHarness> CreateAsync(
        RabbitMqContainerFixture fixture,
        RabbitMqTopologyMode mode = RabbitMqTopologyMode.Declare,
        ushort prefetch = 8,
        int concurrency = 4,
        int maximumAttempts = 3,
        TimeSpan? retryDelay = null,
        bool enableConsumer = false,
        Action<IServiceCollection>? configureServices = null,
        RabbitMqTestTopology? topology = null,
        string? userName = null,
        string? password = null,
        bool validateStartup = true)
    {
        await fixture.EnsureRunningAsync().ConfigureAwait(false);
        topology ??= RabbitMqTestTopology.Create();
        var services = new ServiceCollection();
        services.AddTcjMessaging(options =>
        {
            options.EnableConsumer = enableConsumer;
            options.MaximumConcurrentMessages = concurrency;
            options.MaximumBufferedMessages = Math.Max(prefetch, (ushort)concurrency);
            options.PublishTimeout = TimeSpan.FromSeconds(10);
            options.ShutdownTimeout = TimeSpan.FromSeconds(10);
            options.AdditionalAllowedHeaders.Add("custom-safe");
        });
        services.AddTcjRabbitMq(options =>
        {
            options.HostName = fixture.HostName;
            options.Port = fixture.Port;
            options.UserName = userName ?? fixture.UserName;
            options.Password = password ?? fixture.Password;
            options.VirtualHost = "/";
            options.PrefetchCount = prefetch;
            options.MaximumConcurrentMessages = concurrency;
            options.ConnectionTimeout = TimeSpan.FromSeconds(5);
            options.PublishConfirmTimeout = TimeSpan.FromSeconds(5);
            options.ShutdownTimeout = TimeSpan.FromSeconds(10);
            options.NetworkRecoveryInterval = TimeSpan.FromMilliseconds(250);
            options.AutomaticRecoveryEnabled = true;
            options.TopologyRecoveryEnabled = true;
            options.MandatoryPublish = true;
            options.DefaultExchange = topology.Exchange;
            options.MaximumProcessingAttempts = maximumAttempts;
            options.TopologyMode = mode;
            options.Topology.Exchanges.Add(new RabbitMqExchangeOptions { Name = topology.Exchange, Type = "topic", Durable = true });
            options.Topology.Queues.Add(new RabbitMqQueueOptions { Name = topology.Queue, Durable = true });
            options.Topology.Bindings.Add(new RabbitMqBindingOptions { Exchange = topology.Exchange, Queue = topology.Queue, RoutingKey = topology.RoutingKey });
            options.Topology.RetryTopologies.Add(new RabbitMqRetryTopologyOptions
            {
                SourceQueue = topology.Queue,
                RetryExchange = topology.RetryExchange,
                RetryQueue = topology.RetryQueue,
                RetryRoutingKey = topology.RetryRoutingKey,
                ReturnExchange = topology.Exchange,
                ReturnRoutingKey = topology.RoutingKey,
                DeadLetterExchange = topology.DeadLetterExchange,
                DeadLetterQueue = topology.DeadLetterQueue,
                DeadLetterRoutingKey = topology.DeadLetterRoutingKey,
                RetryDelay = retryDelay ?? TimeSpan.FromMilliseconds(250)
            });
        });
        configureServices?.Invoke(services);
        ServiceProvider provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        var harness = new RabbitMqTestHarness(provider, topology);
        if (validateStartup)
            await provider.GetRequiredService<IMessagingStartupValidator>().ValidateAsync().ConfigureAwait(false);
        return harness;
    }

    internal static TransportMessageEnvelope CreateEnvelope(
        string? id = null,
        string messageType = "test.message",
        int version = 1,
        IReadOnlyDictionary<string, string>? headers = null,
        string payload = "{\"value\":\"ok\"}") =>
        new(id ?? Guid.NewGuid().ToString("N"), messageType, version, Encoding.UTF8.GetBytes(payload), "application/json",
            DateTimeOffset.Parse("2026-09-05T00:00:00Z"), correlationId: "correlation-1", causationId: "cause-1", headers: headers);

    internal async Task<PublishResult> PublishAsync(TransportMessageEnvelope? envelope = null, string? destination = null) =>
        await Publisher.PublishAsync(envelope ?? CreateEnvelope(), new PublishContext { Destination = destination }, CancellationToken.None).ConfigureAwait(false);

    internal async Task<RabbitMqReceivedLease> ReceiveOneAsync(CancellationToken cancellationToken = default)
    {
        IAsyncEnumerator<ReceivedMessage> enumerator = Receiver.ReceiveAsync(
            new ReceiveContext { Source = Topology.Queue, Subscription = "test" }, cancellationToken).GetAsyncEnumerator(cancellationToken);
        try
        {
            if (!await enumerator.MoveNextAsync().ConfigureAwait(false)) throw new InvalidOperationException("RabbitMQ receiver completed before a delivery was available.");
            return new RabbitMqReceivedLease(enumerator, enumerator.Current);
        }
        catch
        {
            await enumerator.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask DisposeAsync() => await Services.DisposeAsync().ConfigureAwait(false);
}

internal sealed record RabbitMqTestTopology(
    string Exchange,
    string Queue,
    string RoutingKey,
    string RetryExchange,
    string RetryQueue,
    string RetryRoutingKey,
    string DeadLetterExchange,
    string DeadLetterQueue,
    string DeadLetterRoutingKey)
{
    internal static RabbitMqTestTopology Create()
    {
        string id = Guid.NewGuid().ToString("N");
        string prefix = $"tcj.test.{id}";
        return new RabbitMqTestTopology(
            $"{prefix}.events", $"{prefix}.queue", "test.message.v1",
            $"{prefix}.retry", $"{prefix}.retry.queue", "retry",
            $"{prefix}.dlx", $"{prefix}.dlq", "dead");
    }
}

internal sealed class StubInboxPipeline : IInboxPipeline
{
    private readonly Func<IncomingMessageEnvelope, CancellationToken, Task<InboxHandlingResult>> _handler;
    internal StubInboxPipeline(Func<IncomingMessageEnvelope, CancellationToken, Task<InboxHandlingResult>> handler) => _handler = handler;
    public Task<InboxHandlingResult> ProcessAsync(IncomingMessageEnvelope envelope, CancellationToken cancellationToken = default) => _handler(envelope, cancellationToken);
}

internal sealed class StubOutboxContextAccessor : IOutboxMessageContextAccessor
{
    public OutboxMessageContext? Current { get; set; }
}

internal sealed class RabbitMqReceivedLease(IAsyncEnumerator<ReceivedMessage> enumerator, ReceivedMessage message) : IAsyncDisposable
{
    internal ReceivedMessage Message { get; } = message;
    public ValueTask DisposeAsync() => enumerator.DisposeAsync();
}
