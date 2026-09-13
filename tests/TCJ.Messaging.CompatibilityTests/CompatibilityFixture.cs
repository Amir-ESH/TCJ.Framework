using Confluent.Kafka;
using Confluent.Kafka.Admin;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TCJ.Messaging.AzureServiceBus.Extensions;
using TCJ.Messaging.AzureServiceBus.Tests.Infrastructure;
using TCJ.Messaging.ConformanceTests;
using TCJ.Messaging.Extensions;
using TCJ.Messaging.HealthChecks;
using TCJ.Messaging.Kafka.Configuration;
using TCJ.Messaging.Kafka.Extensions;
using TCJ.Messaging.Kafka.Tests.Infrastructure;
using TCJ.Messaging.Kafka.Topology;
using TCJ.Messaging.Publishing;
using TCJ.Messaging.RabbitMQ.Tests.Infrastructure;
using TCJ.Messaging.Receiving;
using TCJ.Messaging.Topology;

namespace TCJ.Messaging.CompatibilityTests;

internal sealed class CompatibilityFixture(string transport) : IAsyncDisposable
{
    private RabbitMqContainerFixture? _rabbit;
    private KafkaContainerFixture? _kafka;
    private AzureServiceBusIntegrationEnvironment? _azure;
    internal string Transport { get; } = transport;
    internal ServiceProvider Services { get; private set; } = null!;

    internal async Task InitializeAsync()
    {
        switch (Transport)
        {
            case "RabbitMQ": _rabbit = new(); await _rabbit.InitializeAsync(); break;
            case "Kafka": _kafka = new(); await _kafka.InitializeAsync(); break;
            case "AzureServiceBus":
                _azure = await AzureServiceBusIntegrationEnvironment.CreateAsync()
                    ?? throw new InvalidOperationException("Compatibility requires the repository Service Bus emulator.");
                break;
            case "InMemory": break;
            default: throw new InvalidOperationException("Unknown compatibility transport.");
        }
    }

    internal async ValueTask<MessagingAdapterHarness> CreateAsync(Action<IServiceCollection>? configure = null)
    {
        if (_rabbit is not null)
        {
            var topology = RabbitMqTestTopology.Create() with { RoutingKey = "#" };
            var h = await RabbitMqTestHarness.CreateAsync(_rabbit, topology: topology, configureServices: configure);
            Services = h.Services;
            return new(h.Publisher, h.Services.GetRequiredService<IMessageBatchPublisher>(), h.Receiver, h.Descriptor,
                h.HealthProbe, TimeProvider.System, h.Topology.Queue, h, h.Topology.Exchange, scenarioTimeout: TimeSpan.FromSeconds(25));
        }

        string source = "tcj.compat." + Guid.NewGuid().ToString("N");
        string? group = null;
        var services = new ServiceCollection();
        services.AddTcjMessaging(o => { o.AdditionalAllowedHeaders.Add("custom-safe"); o.PublishTimeout = TimeSpan.FromSeconds(10); o.ShutdownTimeout = TimeSpan.FromSeconds(5); });
        switch (Transport)
        {
            case "InMemory": services.AddTcjInMemoryMessaging(); break;
            case "AzureServiceBus":
                source = await _azure!.CreateQueueAsync();
                services.AddTcjAzureServiceBus(_azure.ConnectionString, o => { o.ManagementConnectionString = _azure.ManagementConnectionString; o.ReadinessDestination = source; o.DefaultRetryDelay = TimeSpan.FromSeconds(1); });
                break;
            case "Kafka":
                using (var admin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = _kafka!.BootstrapServers }).Build())
                    await admin.CreateTopicsAsync([new TopicSpecification { Name = source, NumPartitions = 1, ReplicationFactor = 1 }]);
                group = "compat-" + Guid.NewGuid().ToString("N");
                services.AddTcjKafka(o => { o.BootstrapServers = _kafka.BootstrapServers; o.DefaultTopic = source; o.TopologyMode = KafkaTopologyMode.Disabled; o.AutoOffsetReset = KafkaOffsetResetMode.Earliest; o.PublishTimeout = TimeSpan.FromSeconds(10); o.ShutdownTimeout = TimeSpan.FromSeconds(5); });
                break;
        }
        services.Replace(ServiceDescriptor.Singleton<IMessageTopologyNamingStrategy>(new FixedTopology(source)));
        configure?.Invoke(services);
        var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        Services = provider;
        return new(provider.GetRequiredService<IMessagePublisher>(), provider.GetRequiredService<IMessageBatchPublisher>(),
            provider.GetRequiredService<IMessageReceiver>(), provider.GetRequiredService<MessagingTransportDescriptor>(),
            provider.GetRequiredService<IMessagingTransportHealthProbe>(), TimeProvider.System, source, provider, subscription: group, scenarioTimeout: TimeSpan.FromSeconds(25));
    }

    public async ValueTask DisposeAsync()
    {
        if (_azure is not null) await _azure.DisposeAsync();
        if (_kafka is not null) await _kafka.DisposeAsync();
        if (_rabbit is not null) await _rabbit.DisposeAsync();
    }

    private sealed class FixedTopology(string source) : IMessageTopologyNamingStrategy
    {
        public string GetDestination(string messageType, int messageVersion) => source;
        public string GetSubscription(string consumerName) => consumerName;
    }
}
