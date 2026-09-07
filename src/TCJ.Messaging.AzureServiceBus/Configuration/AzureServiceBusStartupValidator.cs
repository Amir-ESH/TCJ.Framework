using Azure;
using Azure.Messaging.ServiceBus;
using TCJ.Messaging.AzureServiceBus.Connections;
using TCJ.Messaging.AzureServiceBus.Topology;
using TCJ.Messaging.Configuration;

namespace TCJ.Messaging.AzureServiceBus.Configuration;

internal sealed class AzureServiceBusStartupValidator : IMessagingStartupValidator
{
    private readonly MessagingStartupValidator _neutral;
    private readonly AzureServiceBusTopologyManager _topology;
    private readonly AzureServiceBusClientManager _clients;
    private readonly TcjAzureServiceBusOptions _azure;
    private readonly TcjMessagingOptions _messaging;
    private readonly AzureServiceBusAuthentication _authentication;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile bool _validated;

    internal AzureServiceBusStartupValidator(MessagingStartupValidator neutral, AzureServiceBusTopologyManager topology,
        AzureServiceBusClientManager clients, TcjAzureServiceBusOptions azure, TcjMessagingOptions messaging,
        AzureServiceBusAuthentication authentication)
    { _neutral = neutral; _topology = topology; _clients = clients; _azure = azure; _messaging = messaging; _authentication = authentication; }

    public async Task ValidateAsync(CancellationToken cancellationToken = default)
    {
        if (_validated) return;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_validated) return;
            _azure.Validate(_authentication.UsesTokenCredential);
            if (_messaging.EnableConsumer && _azure.MaximumConcurrentMessages != _messaging.MaximumConcurrentMessages)
                throw new InvalidOperationException("Azure Service Bus MaximumConcurrentMessages must match TCJ Messaging MaximumConcurrentMessages when consumer processing is enabled.");
            await _neutral.ValidateAsync(cancellationToken).ConfigureAwait(false);
            _ = await _clients.GetClientAsync(cancellationToken).ConfigureAwait(false);
            try { await _topology.EnsureAsync(cancellationToken).ConfigureAwait(false); }
            catch (UnauthorizedAccessException) { throw new InvalidOperationException("Azure Service Bus startup validation failed (PermanentAuthorization)."); }
            catch (RequestFailedException exception) when (exception.Status is 401 or 403) { throw new InvalidOperationException("Azure Service Bus startup validation failed (PermanentAuthorization)."); }
            catch (RequestFailedException exception) when (exception.Status is 408 or 429 || exception.Status >= 500) { throw new InvalidOperationException("Azure Service Bus startup validation failed (TransientConnection)."); }
            catch (RequestFailedException) { throw new InvalidOperationException("Azure Service Bus startup validation failed (PermanentTopology)."); }
            catch (ServiceBusException exception) when (!exception.IsTransient) { throw new InvalidOperationException("Azure Service Bus startup validation failed (PermanentTopology)."); }
            catch (ServiceBusException) { throw new InvalidOperationException("Azure Service Bus startup validation failed (TransientConnection)."); }
            _validated = true;
        }
        finally { _gate.Release(); }
    }
}
