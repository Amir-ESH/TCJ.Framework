using TCJ.Messaging.AzureServiceBus.Configuration;
using TCJ.Messaging.AzureServiceBus.Connections;
using TCJ.Messaging.HealthChecks;

namespace TCJ.Messaging.AzureServiceBus.HealthChecks;

internal sealed class AzureServiceBusTransportHealthProbe : IMessagingTransportHealthProbe
{
    private readonly AzureServiceBusClientManager _clients;
    private readonly TcjAzureServiceBusOptions _options;
    internal AzureServiceBusTransportHealthProbe(AzureServiceBusClientManager clients, TcjAzureServiceBusOptions options) { _clients = clients; _options = options; }
    public async ValueTask<bool> IsReadyAsync(CancellationToken cancellationToken = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(_options.TryTimeout);
        try { return await _clients.ProbeReadinessAsync(cts.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { return false; }
    }
}
