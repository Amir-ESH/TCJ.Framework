using TCJ.Messaging.RabbitMQ.Configuration;
using TCJ.Messaging.Topology;

namespace TCJ.Messaging.RabbitMQ.Topology;

internal sealed class RabbitMqMessageTopologyNamingStrategy : IMessageTopologyNamingStrategy
{
    private readonly TcjRabbitMqOptions _options;
    internal RabbitMqMessageTopologyNamingStrategy(TcjRabbitMqOptions options) => _options = options ?? throw new ArgumentNullException(nameof(options));
    public string GetDestination(string messageType, int messageVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(messageType);
        if (messageVersion <= 0) throw new ArgumentOutOfRangeException(nameof(messageVersion));
        return _options.DefaultExchange;
    }
}
