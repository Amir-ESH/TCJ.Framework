using RabbitMQ.Client;

namespace TCJ.Messaging.RabbitMQ.Tests.Infrastructure;

internal sealed class RabbitMqBrokerAdmin : IAsyncDisposable
{
    private readonly IConnection _connection;
    private readonly IChannel _channel;

    private RabbitMqBrokerAdmin(IConnection connection, IChannel channel)
    {
        _connection = connection;
        _channel = channel;
    }

    internal static async Task<RabbitMqBrokerAdmin> CreateAsync(RabbitMqContainerFixture fixture, CancellationToken cancellationToken = default)
    {
        IConnection connection = await fixture.CreateConnectionFactory().CreateConnectionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            IChannel channel = await connection.CreateChannelAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            return new RabbitMqBrokerAdmin(connection, channel);
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    internal IChannel Channel => _channel;

    internal async Task<uint> MessageCountAsync(string queue, CancellationToken cancellationToken = default)
    {
        QueueDeclareOk result = await _channel.QueueDeclarePassiveAsync(queue, cancellationToken).ConfigureAwait(false);
        return result.MessageCount;
    }

    internal Task<BasicGetResult?> GetAsync(string queue, bool autoAck = true, CancellationToken cancellationToken = default) =>
        _channel.BasicGetAsync(queue, autoAck, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        await _channel.DisposeAsync().ConfigureAwait(false);
        await _connection.DisposeAsync().ConfigureAwait(false);
    }
}
