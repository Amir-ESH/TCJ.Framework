using TCJ.Messaging.HealthChecks;
using TCJ.Messaging.RabbitMQ.Configuration;
using TCJ.Messaging.RabbitMQ.Connections;

namespace TCJ.Messaging.RabbitMQ.HealthChecks;

internal sealed class RabbitMqTransportHealthProbe : IMessagingTransportHealthProbe
{
    private readonly RabbitMqConnectionManager _connections;
    private readonly TcjRabbitMqOptions _options;
    internal RabbitMqTransportHealthProbe(RabbitMqConnectionManager connections, TcjRabbitMqOptions options)
    { _connections = connections; _options = options; }
    public async ValueTask<bool> IsReadyAsync(CancellationToken cancellationToken = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(_options.ConnectionTimeout);
        try { _ = await _connections.GetConnectionAsync(cts.Token).ConfigureAwait(false); return true; }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { return false; }
    }
}
