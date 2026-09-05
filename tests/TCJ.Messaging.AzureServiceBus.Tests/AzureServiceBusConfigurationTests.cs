using Azure.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TCJ.Messaging.AzureServiceBus.Configuration;
using TCJ.Messaging.AzureServiceBus.Extensions;
using TCJ.Messaging.AzureServiceBus.Publishing;
using TCJ.Messaging.AzureServiceBus.Topology;
using TCJ.Messaging.Configuration;
using TCJ.Messaging.Envelopes;
using TCJ.Messaging.Extensions;
using TCJ.Messaging.Publishing;

namespace TCJ.Messaging.AzureServiceBus.Tests;

public sealed class AzureServiceBusConfigurationTests
{
    private const string TestConnection = "Endpoint=sb://localhost;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true;";

    [Fact]
    public void Registration_requires_neutral_messaging()
    {
        var services = new ServiceCollection();
        Assert.Throws<InvalidOperationException>(() => services.AddTcjAzureServiceBus(TestConnection));
    }

    [Fact]
    public void Descriptor_declares_only_implemented_capabilities()
    {
        using ServiceProvider provider = BuildProvider();
        MessagingTransportDescriptor descriptor = provider.GetRequiredService<MessagingTransportDescriptor>();
        Assert.Equal("AzureServiceBus", descriptor.Name);
        Assert.True(descriptor.Capabilities.SupportsBatchPublish);
        Assert.True(descriptor.Capabilities.SupportsScheduling);
        Assert.True(descriptor.Capabilities.SupportsTimeToLive);
        Assert.True(descriptor.Capabilities.SupportsDeadLetter);
        Assert.True(descriptor.Capabilities.SupportsDefer);
        Assert.True(descriptor.Capabilities.SupportsPeekLock);
        Assert.True(descriptor.Capabilities.SupportsOrderedDelivery);
        Assert.Equal(MessagingOrderingGuarantee.PerSession, descriptor.Capabilities.OrderingGuarantee);
        Assert.False(descriptor.Capabilities.SupportsTransactions);
    }

    [Fact]
    public void Auto_completion_cannot_be_enabled()
    {
        var services = new ServiceCollection();
        services.AddTcjMessaging();
        Assert.Throws<ArgumentException>(() => services.AddTcjAzureServiceBus(TestConnection, options => options.AutoCompleteMessages = true));
    }

    [Fact]
    public void Per_session_parallelism_must_remain_one()
    {
        var services = new ServiceCollection();
        services.AddTcjMessaging();
        Assert.Throws<ArgumentOutOfRangeException>(() => services.AddTcjAzureServiceBus(TestConnection, options => options.MaximumConcurrentCallsPerSession = 2));
    }

    [Fact]
    public void Malformed_connection_string_is_rejected_without_echoing_secret()
    {
        var services = new ServiceCollection();
        services.AddTcjMessaging();
        ArgumentException exception = Assert.Throws<ArgumentException>(() => services.AddTcjAzureServiceBus("not-a-service-bus-secret"));
        Assert.DoesNotContain("not-a-service-bus-secret", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Token_credential_requires_namespace_and_supports_managed_identity_shape()
    {
        var services = new ServiceCollection();
        services.AddTcjMessaging();
        services.AddTcjAzureServiceBus("example.servicebus.windows.net", new TestTokenCredential());
        Assert.Contains(services, static descriptor => descriptor.ServiceType == typeof(IMessagingTransportPublisher));
    }

    [Fact]
    public void Duplicate_transport_registration_is_rejected()
    {
        var services = new ServiceCollection();
        services.AddTcjMessaging();
        services.AddTcjAzureServiceBus(TestConnection);
        Assert.Throws<InvalidOperationException>(() => services.AddTcjAzureServiceBus(TestConnection));
    }

    [Fact]
    public void Topology_rejects_queue_topic_name_conflict()
    {
        var services = new ServiceCollection();
        services.AddTcjMessaging();
        Assert.Throws<ArgumentException>(() => services.AddTcjAzureServiceBus(TestConnection, options =>
        {
            options.Topology.Queues.Add(new AzureServiceBusQueueOptions { Name = "orders" });
            options.Topology.Topics.Add(new AzureServiceBusTopicOptions { Name = "orders" });
        }));
    }

    [Fact]
    public void Topology_rejects_session_subscription_without_declared_topic()
    {
        var services = new ServiceCollection();
        services.AddTcjMessaging();
        Assert.Throws<ArgumentException>(() => services.AddTcjAzureServiceBus(TestConnection, options =>
            options.Topology.Subscriptions.Add(new AzureServiceBusSubscriptionOptions { TopicName = "missing", SubscriptionName = "consumer", RequiresSession = true })));
    }

    [Fact]
    public void Mapper_preserves_stable_logical_metadata_and_filters_credentials()
    {
        var messaging = new TcjMessagingOptions();
        messaging.AdditionalAllowedHeaders.Add("custom-safe");
        var headerPolicy = new MessagingHeaderPolicy(messaging);
        var options = new TcjAzureServiceBusOptions { ConnectionString = TestConnection };
        var mapper = new AzureServiceBusMessageMapper(headerPolicy, options, new FakeTimeProvider(DateTimeOffset.Parse("2026-09-06T00:00:00Z")));
        var envelope = new TransportMessageEnvelope("message-1", "orders.created", 2, "{}"u8.ToArray(), "application/json",
            DateTimeOffset.Parse("2026-09-06T00:00:00Z"), "corr-1", "cause-1", headers: new Dictionary<string, string>
            {
                ["custom-safe"] = "safe",
                ["authorization"] = "Bearer secret"
            });
        Azure.Messaging.ServiceBus.ServiceBusMessage mapped = mapper.ToServiceBusMessage(envelope, new PublishContext { Destination = "orders" });
        Assert.Equal("message-1", mapped.MessageId);
        Assert.Equal("orders.created", mapped.Subject);
        Assert.Equal("corr-1", mapped.CorrelationId);
        Assert.Equal("safe", mapped.ApplicationProperties["custom-safe"]);
        Assert.False(mapped.ApplicationProperties.ContainsKey("authorization"));
        Assert.Equal("cause-1", mapped.ApplicationProperties["tcj-causation-id"]);
    }

    [Fact]
    public void Mapper_rejects_mismatched_session_and_partition_keys()
    {
        var messaging = new TcjMessagingOptions();
        var mapper = new AzureServiceBusMessageMapper(new MessagingHeaderPolicy(messaging), new TcjAzureServiceBusOptions { ConnectionString = TestConnection }, TimeProvider.System);
        var envelope = new TransportMessageEnvelope("message-1", "orders.created", 1, "{}"u8.ToArray(), "application/json", DateTimeOffset.UtcNow, orderingKey: "session-a");

        ArgumentException exception = Assert.Throws<ArgumentException>(() => mapper.ToServiceBusMessage(envelope,
            new PublishContext { Destination = "orders", PartitionKey = "partition-b" }));

        Assert.Contains("identical", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Topology_exposes_explicit_partitioning_expectations()
    {
        var queue = new AzureServiceBusQueueOptions { Name = "orders", EnablePartitioning = true };
        var topic = new AzureServiceBusTopicOptions { Name = "events", EnablePartitioning = true };

        Assert.True(queue.EnablePartitioning);
        Assert.True(topic.EnablePartitioning);
    }

    [Fact]
    public void Mapper_rejects_non_future_schedule()
    {
        var messaging = new TcjMessagingOptions();
        var mapper = new AzureServiceBusMessageMapper(new MessagingHeaderPolicy(messaging), new TcjAzureServiceBusOptions { ConnectionString = TestConnection },
            new FakeTimeProvider(DateTimeOffset.Parse("2026-09-06T00:00:00Z")));
        var envelope = new TransportMessageEnvelope("message-1", "orders.created", 1, "{}"u8.ToArray(), "application/json", DateTimeOffset.Parse("2026-09-06T00:00:00Z"));
        Assert.Throws<ArgumentOutOfRangeException>(() => mapper.ToServiceBusMessage(envelope,
            new PublishContext { Destination = "orders", ScheduledAtUtc = DateTimeOffset.Parse("2026-09-05T23:59:59Z") }));
    }

    [Fact]
    public void Mapper_rejects_non_positive_ttl()
    {
        var messaging = new TcjMessagingOptions();
        var mapper = new AzureServiceBusMessageMapper(new MessagingHeaderPolicy(messaging), new TcjAzureServiceBusOptions { ConnectionString = TestConnection }, TimeProvider.System);
        var envelope = new TransportMessageEnvelope("message-1", "orders.created", 1, "{}"u8.ToArray(), "application/json", DateTimeOffset.UtcNow);
        Assert.Throws<ArgumentOutOfRangeException>(() => mapper.ToServiceBusMessage(envelope, new PublishContext { Destination = "orders", TimeToLive = TimeSpan.Zero }));
    }

    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddTcjMessaging();
        services.AddTcjAzureServiceBus(TestConnection);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    private sealed class TestTokenCredential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) => new("test", DateTimeOffset.MaxValue);
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) => ValueTask.FromResult(new AccessToken("test", DateTimeOffset.MaxValue));
    }
}
