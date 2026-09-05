using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;

namespace TCJ.Messaging.AzureServiceBus.Tests.Infrastructure;

internal sealed class AzureServiceBusIntegrationEnvironment : IAsyncDisposable
{
    private readonly List<(string Kind, string Name, string? Subscription)> _resources = [];

    private AzureServiceBusIntegrationEnvironment(string connectionString, string managementConnectionString)
    {
        ConnectionString = connectionString;
        ManagementConnectionString = managementConnectionString;
        Client = new ServiceBusClient(connectionString, new ServiceBusClientOptions
        {
            RetryOptions = new ServiceBusRetryOptions
            {
                MaxRetries = 2,
                Delay = TimeSpan.FromMilliseconds(250),
                MaxDelay = TimeSpan.FromSeconds(2),
                TryTimeout = TimeSpan.FromSeconds(10),
                Mode = ServiceBusRetryMode.Exponential
            }
        });
        Administration = new ServiceBusAdministrationClient(managementConnectionString);
    }

    internal string ConnectionString { get; }
    internal string ManagementConnectionString { get; }
    internal ServiceBusClient Client { get; }
    internal ServiceBusAdministrationClient Administration { get; }

    internal static ValueTask<AzureServiceBusIntegrationEnvironment?> CreateAsync()
    {
        string? connection = Environment.GetEnvironmentVariable("TCJ_AZURE_SERVICE_BUS_CONNECTION_STRING");
        string? management = Environment.GetEnvironmentVariable("TCJ_AZURE_SERVICE_BUS_MANAGEMENT_CONNECTION_STRING") ?? connection;
        bool required = string.Equals(Environment.GetEnvironmentVariable("TCJ_AZURE_SERVICE_BUS_REQUIRE_INTEGRATION"), "1", StringComparison.Ordinal);
        if (string.IsNullOrWhiteSpace(connection) || string.IsNullOrWhiteSpace(management))
        {
            if (required)
                throw new InvalidOperationException("Azure Service Bus integration is required but TCJ_AZURE_SERVICE_BUS_CONNECTION_STRING / management connection are not configured.");
            return ValueTask.FromResult<AzureServiceBusIntegrationEnvironment?>(null);
        }
        return ValueTask.FromResult<AzureServiceBusIntegrationEnvironment?>(new AzureServiceBusIntegrationEnvironment(connection, management));
    }

    internal Task<string> CreateQueueAsync(bool sessions = false, bool duplicateDetection = false,
        bool deadLetterOnExpiration = false, TimeSpan? defaultTtl = null, int? maxDeliveryCount = null) =>
        CreateQueueAsync(CreateName("q"), sessions, duplicateDetection, deadLetterOnExpiration, defaultTtl, maxDeliveryCount);

    internal async Task<string> CreateQueueAsync(string name, bool sessions = false, bool duplicateDetection = false,
        bool deadLetterOnExpiration = false, TimeSpan? defaultTtl = null, int? maxDeliveryCount = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var options = new CreateQueueOptions(name)
        {
            RequiresSession = sessions,
            RequiresDuplicateDetection = duplicateDetection,
            DeadLetteringOnMessageExpiration = deadLetterOnExpiration
        };
        if (duplicateDetection) options.DuplicateDetectionHistoryTimeWindow = TimeSpan.FromSeconds(20);
        if (defaultTtl is { } ttl) options.DefaultMessageTimeToLive = ttl;
        if (maxDeliveryCount is { } count) options.MaxDeliveryCount = count;
        await Administration.CreateQueueAsync(options).ConfigureAwait(false);
        _resources.Add(("queue", name, null));
        return name;
    }

    internal async Task<(string Topic, string Subscription)> CreateTopicSubscriptionAsync(bool sessions = false)
    {
        string topic = CreateName("t");
        string subscription = CreateName("s", 42);
        await Administration.CreateTopicAsync(new CreateTopicOptions(topic)).ConfigureAwait(false);
        await Administration.CreateSubscriptionAsync(new CreateSubscriptionOptions(topic, subscription)
        {
            RequiresSession = sessions
        }).ConfigureAwait(false);
        _resources.Add(("topic", topic, subscription));
        return (topic, subscription);
    }

    internal async Task<ServiceBusReceivedMessage> ReceiveAsync(string queue, TimeSpan? wait = null)
    {
        await using ServiceBusReceiver receiver = Client.CreateReceiver(queue, new ServiceBusReceiverOptions { ReceiveMode = ServiceBusReceiveMode.PeekLock });
        ServiceBusReceivedMessage? message = await receiver.ReceiveMessageAsync(wait ?? TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        return message ?? throw new InvalidOperationException($"Expected a Service Bus message from '{queue}'.");
    }

    internal static ServiceBusMessage Message(string id, string body = "{}") => new(body)
    {
        MessageId = id,
        Subject = "tcj.test.message",
        ContentType = "application/json",
        CorrelationId = "corr-1"
    };

    private static string CreateName(string kind, int maximum = 50)
    {
        string value = $"tcj-s48-{kind}-{Guid.NewGuid():N}";
        return value.Length <= maximum ? value : value[..maximum];
    }

    public async ValueTask DisposeAsync()
    {
        foreach ((string kind, string name, _) in _resources.AsEnumerable().Reverse())
        {
            try
            {
                if (kind == "queue" && (await Administration.QueueExistsAsync(name).ConfigureAwait(false)).Value)
                    await Administration.DeleteQueueAsync(name).ConfigureAwait(false);
                else if (kind == "topic" && (await Administration.TopicExistsAsync(name).ConfigureAwait(false)).Value)
                    await Administration.DeleteTopicAsync(name).ConfigureAwait(false);
            }
            catch
            {
                // Best-effort cleanup; the dedicated workflow also tears down the isolated emulator.
            }
        }
        await Client.DisposeAsync().ConfigureAwait(false);
    }
}
