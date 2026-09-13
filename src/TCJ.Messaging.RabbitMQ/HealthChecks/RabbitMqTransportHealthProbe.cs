using TCJ.Messaging.HealthChecks;
using TCJ.Messaging.RabbitMQ.Connections;

namespace TCJ.Messaging.RabbitMQ.HealthChecks;

internal sealed class RabbitMqTransportHealthProbe : IMessagingTransportHealthProbe
{
    private readonly RabbitMqConnectionManager _connections;
    public RabbitMqTransportHealthProbe(RabbitMqConnectionManager connections) => _connections = connections;

    public async ValueTask<bool> IsReadyAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            _ = await _connections.GetConnectionAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }
}
