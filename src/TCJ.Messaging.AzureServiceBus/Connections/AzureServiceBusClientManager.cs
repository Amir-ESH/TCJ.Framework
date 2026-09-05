using Azure.Core;
using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using TCJ.Messaging.AzureServiceBus.Configuration;
using TCJ.Messaging.AzureServiceBus.Diagnostics;

namespace TCJ.Messaging.AzureServiceBus.Connections;

internal sealed class AzureServiceBusAuthentication
{
    internal AzureServiceBusAuthentication(TokenCredential? credential) => Credential = credential;
    internal TokenCredential? Credential { get; }
    internal bool UsesTokenCredential => Credential is not null;
}

internal interface IAzureServiceBusClientFactory
{
    ServiceBusClient CreateClient(TcjAzureServiceBusOptions options, AzureServiceBusAuthentication authentication);
    ServiceBusAdministrationClient CreateAdministrationClient(TcjAzureServiceBusOptions options, AzureServiceBusAuthentication authentication);
}

internal sealed class DefaultAzureServiceBusClientFactory : IAzureServiceBusClientFactory
{
    public ServiceBusClient CreateClient(TcjAzureServiceBusOptions options, AzureServiceBusAuthentication authentication)
    {
        ServiceBusClientOptions clientOptions = CreateClientOptions(options);
        return authentication.Credential is TokenCredential credential
            ? new ServiceBusClient(options.FullyQualifiedNamespace!, credential, clientOptions)
            : new ServiceBusClient(options.ConnectionString!, clientOptions);
    }

    public ServiceBusAdministrationClient CreateAdministrationClient(TcjAzureServiceBusOptions options, AzureServiceBusAuthentication authentication)
    {
        if (!string.IsNullOrWhiteSpace(options.ManagementConnectionString))
            return new ServiceBusAdministrationClient(options.ManagementConnectionString);
        return authentication.Credential is TokenCredential credential
            ? new ServiceBusAdministrationClient(options.FullyQualifiedNamespace!, credential)
            : new ServiceBusAdministrationClient(options.ConnectionString!);
    }

    private static ServiceBusClientOptions CreateClientOptions(TcjAzureServiceBusOptions options) => new()
    {
        RetryOptions = new ServiceBusRetryOptions
        {
            MaxRetries = options.MaximumRetries,
            Delay = options.RetryDelay,
            MaxDelay = options.MaximumRetryDelay,
            TryTimeout = options.TryTimeout,
            Mode = options.RetryMode == AzureServiceBusRetryMode.Exponential ? ServiceBusRetryMode.Exponential : ServiceBusRetryMode.Fixed
        }
    };
}

internal sealed class AzureServiceBusClientManager : IAsyncDisposable
{
    private readonly TcjAzureServiceBusOptions _options;
    private readonly AzureServiceBusAuthentication _authentication;
    private readonly IAzureServiceBusClientFactory _factory;
    private readonly SemaphoreSlim _clientGate = new(1, 1);
    private readonly SemaphoreSlim _senderGate = new(1, 1);
    private readonly Dictionary<string, ServiceBusSender> _senders = new(StringComparer.OrdinalIgnoreCase);
    private ServiceBusClient? _client;
    private int _disposed;
    private string? _lastFailureType;

    internal AzureServiceBusClientManager(TcjAzureServiceBusOptions options, AzureServiceBusAuthentication authentication, IAzureServiceBusClientFactory factory)
    { _options = options; _authentication = authentication; _factory = factory; }

    internal bool IsClientOpen => _client is { IsClosed: false } && Volatile.Read(ref _disposed) == 0;
    internal string? LastFailureType => Volatile.Read(ref _lastFailureType);

    internal async ValueTask<ServiceBusClient> GetClientAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ServiceBusClient? client = Volatile.Read(ref _client);
        if (client is { IsClosed: false }) return client;
        await _clientGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            client = _client;
            if (client is { IsClosed: false }) return client;
            using var activity = AzureServiceBusDiagnostics.Start(TcjAzureServiceBusDiagnosticNames.ConnectActivity, "connect");
            try
            {
                client = _factory.CreateClient(_options, _authentication);
                _client = client;
                Volatile.Write(ref _lastFailureType, null);
                return client;
            }
            catch (Exception exception)
            {
                Volatile.Write(ref _lastFailureType, AzureServiceBusFailureClassifier.FailureType(exception));
                activity?.SetStatus(System.Diagnostics.ActivityStatusCode.Error, LastFailureType);
                throw;
            }
        }
        finally { _clientGate.Release(); }
    }

    internal async ValueTask<ServiceBusSender> GetSenderAsync(string destination, CancellationToken cancellationToken = default)
    {
        AzureServiceBusValidation.ValidateEntityName(destination, nameof(destination));
        ThrowIfDisposed();
        await _senderGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (_senders.TryGetValue(destination, out ServiceBusSender? cached))
            {
                if (!cached.IsClosed) return cached;
                _senders.Remove(destination);
                await cached.DisposeAsync().ConfigureAwait(false);
            }
            if (_senders.Count >= _options.MaximumSenderCacheSize)
                throw new InvalidOperationException("Azure Service Bus sender cache reached the configured bounded destination limit (PermanentTopology).");
            ServiceBusClient client = await GetClientAsync(cancellationToken).ConfigureAwait(false);
            ServiceBusSender sender = client.CreateSender(destination);
            _senders.Add(destination, sender);
            return sender;
        }
        finally { _senderGate.Release(); }
    }

    internal async ValueTask<ServiceBusReceiver> CreateReceiverAsync(string source, string? subscription, CancellationToken cancellationToken)
    {
        ServiceBusClient client = await GetClientAsync(cancellationToken).ConfigureAwait(false);
        var options = new ServiceBusReceiverOptions { ReceiveMode = ServiceBusReceiveMode.PeekLock, PrefetchCount = _options.PrefetchCount };
        return subscription is null ? client.CreateReceiver(source, options) : client.CreateReceiver(source, subscription, options);
    }

    internal async ValueTask<ServiceBusSessionReceiver> AcceptNextSessionAsync(string source, string? subscription, CancellationToken cancellationToken)
    {
        ServiceBusClient client = await GetClientAsync(cancellationToken).ConfigureAwait(false);
        var options = new ServiceBusSessionReceiverOptions { ReceiveMode = ServiceBusReceiveMode.PeekLock, PrefetchCount = _options.PrefetchCount };
        return subscription is null
            ? await client.AcceptNextSessionAsync(source, options, cancellationToken).ConfigureAwait(false)
            : await client.AcceptNextSessionAsync(source, subscription, options, cancellationToken).ConfigureAwait(false);
    }

    internal ServiceBusAdministrationClient CreateAdministrationClient()
    {
        ThrowIfDisposed();
        return _factory.CreateAdministrationClient(_options, _authentication);
    }

    internal async ValueTask<bool> ProbeReadinessAsync(CancellationToken cancellationToken)
    {
        ServiceBusClient client = await GetClientAsync(cancellationToken).ConfigureAwait(false);
        string? destination = ResolveReadinessDestination();
        if (destination is null) return !client.IsClosed;

        ServiceBusSender sender = await GetSenderAsync(destination, cancellationToken).ConfigureAwait(false);
        using ServiceBusMessageBatch batch = await sender.CreateMessageBatchAsync(cancellationToken).ConfigureAwait(false);
        RecordSuccess();
        return !sender.IsClosed;
    }

    private string? ResolveReadinessDestination()
    {
        if (!string.IsNullOrWhiteSpace(_options.ReadinessDestination)) return _options.ReadinessDestination;
        TCJ.Messaging.AzureServiceBus.Topology.AzureServiceBusQueueOptions? queue = _options.Topology.Queues.FirstOrDefault();
        if (queue is not null) return queue.Name;
        TCJ.Messaging.AzureServiceBus.Topology.AzureServiceBusTopicOptions? topic = _options.Topology.Topics.FirstOrDefault();
        return topic?.Name;
    }

    internal void RecordFailure(Exception exception) => Volatile.Write(ref _lastFailureType, AzureServiceBusFailureClassifier.FailureType(exception));
    internal void RecordSuccess() => Volatile.Write(ref _lastFailureType, null);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await _senderGate.WaitAsync().ConfigureAwait(false);
        try
        {
            foreach (ServiceBusSender sender in _senders.Values)
            {
                try { await sender.DisposeAsync().ConfigureAwait(false); } catch { }
            }
            _senders.Clear();
        }
        finally { _senderGate.Release(); }
        ServiceBusClient? client = Interlocked.Exchange(ref _client, null);
        if (client is not null)
        {
            try { await client.DisposeAsync().ConfigureAwait(false); } catch { }
        }
        _senderGate.Dispose();
        _clientGate.Dispose();
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0) throw new ObjectDisposedException(nameof(AzureServiceBusClientManager));
    }
}

internal static class AzureServiceBusFailureClassifier
{
    internal static (TCJ.Messaging.Publishing.PublishOutcome Outcome, TCJ.Messaging.Publishing.MessagingFailureCategory Category, string Type) ClassifyPublish(Exception exception)
    {
        if (exception is OperationCanceledException) return (TCJ.Messaging.Publishing.PublishOutcome.Canceled, TCJ.Messaging.Publishing.MessagingFailureCategory.Canceled, "Canceled");
        if (exception is TimeoutException) return (TCJ.Messaging.Publishing.PublishOutcome.TimedOut, TCJ.Messaging.Publishing.MessagingFailureCategory.TransientTimeout, "ServiceTimeout");
        if (exception is UnauthorizedAccessException) return (TCJ.Messaging.Publishing.PublishOutcome.PermanentFailure, TCJ.Messaging.Publishing.MessagingFailureCategory.PermanentAuthorization, "PermanentAuthorization");
        if (exception is ServiceBusException serviceBus)
        {
            if (serviceBus.Reason is ServiceBusFailureReason.MessageLockLost or ServiceBusFailureReason.SessionLockLost)
                return (TCJ.Messaging.Publishing.PublishOutcome.PermanentFailure, TCJ.Messaging.Publishing.MessagingFailureCategory.Unknown, "LockLost");
            if (serviceBus.Reason == ServiceBusFailureReason.MessageSizeExceeded)
                return (TCJ.Messaging.Publishing.PublishOutcome.PermanentFailure, TCJ.Messaging.Publishing.MessagingFailureCategory.PayloadTooLarge, "PayloadTooLarge");
            if (serviceBus.Reason == ServiceBusFailureReason.MessagingEntityNotFound || serviceBus.Reason == ServiceBusFailureReason.MessagingEntityDisabled)
                return (TCJ.Messaging.Publishing.PublishOutcome.PermanentFailure, TCJ.Messaging.Publishing.MessagingFailureCategory.PermanentTopology, "PermanentTopology");
            if (serviceBus.Reason == ServiceBusFailureReason.ServiceTimeout)
                return (TCJ.Messaging.Publishing.PublishOutcome.TimedOut, TCJ.Messaging.Publishing.MessagingFailureCategory.TransientTimeout, "ServiceTimeout");
            if (serviceBus.Reason is ServiceBusFailureReason.ServiceBusy or ServiceBusFailureReason.QuotaExceeded)
                return (TCJ.Messaging.Publishing.PublishOutcome.TransientFailure, TCJ.Messaging.Publishing.MessagingFailureCategory.TransientThrottle, "TransientThrottle");
            if (serviceBus.IsTransient)
                return (TCJ.Messaging.Publishing.PublishOutcome.TransientFailure, TCJ.Messaging.Publishing.MessagingFailureCategory.TransientConnection, "TransientConnection");
        }
        return (TCJ.Messaging.Publishing.PublishOutcome.PermanentFailure, TCJ.Messaging.Publishing.MessagingFailureCategory.Unknown, "ServiceBusFailure");
    }

    internal static bool IsLockLost(Exception exception) => exception is ServiceBusException { Reason: ServiceBusFailureReason.MessageLockLost or ServiceBusFailureReason.SessionLockLost };

    internal static string FailureType(Exception exception)
    {
        var classified = ClassifyPublish(exception);
        return classified.Type;
    }
}
