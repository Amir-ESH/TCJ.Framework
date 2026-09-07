using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using TCJ.Messaging.AzureServiceBus.Configuration;
using TCJ.Messaging.AzureServiceBus.Connections;

namespace TCJ.Concurrency.Tests.Tests;

public sealed class AzureServiceBusStressTests
{
    [Fact]
    [Trait("Category", "Concurrency")]
    [Trait("Category", "AzureServiceBus")]
    public async Task Concurrent_sender_requests_reuse_one_sender_per_destination()
    {
        var client = new TestServiceBusClient();
        await using var manager = CreateManager(client, maximumSenderCacheSize: 8);

        ServiceBusSender[] senders = await Task.WhenAll(
            Enumerable.Range(0, 128)
                .Select(async _ => await manager.GetSenderAsync("orders")));

        Assert.All(senders, sender => Assert.Same(client.Sender, sender));
        Assert.Equal(1, client.CreateSenderCalls);
    }

    [Fact]
    [Trait("Category", "Concurrency")]
    [Trait("Category", "AzureServiceBus")]
    public async Task Sender_cache_remains_bounded_under_concurrent_destination_growth()
    {
        var client = new TestServiceBusClient();
        await using var manager = CreateManager(client, maximumSenderCacheSize: 4);

        Exception?[] failures = await Task.WhenAll(
            Enumerable.Range(0, 32)
                .Select(async index =>
                {
                    try
                    {
                        _ = await manager.GetSenderAsync($"orders-{index:D2}");
                        return null;
                    }
                    catch (Exception exception)
                    {
                        return exception;
                    }
                }));

        Assert.Equal(4, client.CreateSenderCalls);
        Assert.Equal(28, failures.Count(static failure => failure is InvalidOperationException));
        Assert.DoesNotContain(failures, static failure => failure is not null && failure is not InvalidOperationException);
    }

    private static AzureServiceBusClientManager CreateManager(TestServiceBusClient client, int maximumSenderCacheSize)
    {
        var options = new TcjAzureServiceBusOptions
        {
            ConnectionString = "Endpoint=sb://localhost/;SharedAccessKeyName=local;SharedAccessKey=local",
            MaximumSenderCacheSize = maximumSenderCacheSize
        };
        return new AzureServiceBusClientManager(
            options,
            new AzureServiceBusAuthentication(null),
            new TestClientFactory(client));
    }

    private sealed class TestClientFactory(TestServiceBusClient client) : IAzureServiceBusClientFactory
    {
        public ServiceBusClient CreateClient(TcjAzureServiceBusOptions options, AzureServiceBusAuthentication authentication) => client;

        public ServiceBusAdministrationClient CreateAdministrationClient(
            TcjAzureServiceBusOptions options,
            AzureServiceBusAuthentication authentication) => throw new NotSupportedException();
    }

    private sealed class TestServiceBusClient : ServiceBusClient
    {
        private readonly TestServiceBusSender _sender = new();
        private int _createSenderCalls;

        internal ServiceBusSender Sender => _sender;
        internal int CreateSenderCalls => Volatile.Read(ref _createSenderCalls);

        public override ServiceBusSender CreateSender(string queueOrTopicName)
        {
            Interlocked.Increment(ref _createSenderCalls);
            return _sender;
        }
    }

    private sealed class TestServiceBusSender : ServiceBusSender
    {
        public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
