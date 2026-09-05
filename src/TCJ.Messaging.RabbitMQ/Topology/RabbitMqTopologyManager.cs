using System.Diagnostics;
using RabbitMQ.Client;
using TCJ.Messaging.RabbitMQ.Configuration;
using TCJ.Messaging.RabbitMQ.Connections;
using TCJ.Messaging.RabbitMQ.Diagnostics;

namespace TCJ.Messaging.RabbitMQ.Topology;

internal sealed class RabbitMqTopologyManager
{
    private readonly RabbitMqConnectionManager _connections;
    private readonly TcjRabbitMqOptions _options;

    internal RabbitMqTopologyManager(RabbitMqConnectionManager connections, TcjRabbitMqOptions options)
    {
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    internal async Task EnsureAsync(CancellationToken cancellationToken)
    {
        _options.Validate();
        if (_options.TopologyMode == RabbitMqTopologyMode.Disabled) return;
        IConnection connection = await _connections.GetConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using IChannel channel = await connection.CreateChannelAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        RabbitMqDiagnostics.ChannelOpened();
        try
        {
            using Activity? activity = RabbitMqDiagnostics.Start(TcjRabbitMqDiagnosticNames.TopologyDeclareActivity,
                _options.TopologyMode == RabbitMqTopologyMode.Declare ? "topology.declare" : "topology.validate");
            if (_options.TopologyMode == RabbitMqTopologyMode.ValidateOnly)
                await ValidateOnlyAsync(channel, cancellationToken).ConfigureAwait(false);
            else
                await DeclareAsync(channel, cancellationToken).ConfigureAwait(false);
            activity?.SetStatus(ActivityStatusCode.Ok);
        }
        finally
        {
            RabbitMqDiagnostics.ChannelClosed();
        }
    }

    private async Task DeclareAsync(IChannel channel, CancellationToken cancellationToken)
    {
        var declaredExchanges = new HashSet<string>(StringComparer.Ordinal);
        foreach (RabbitMqExchangeOptions exchange in _options.Topology.Exchanges)
        {
            await channel.ExchangeDeclareAsync(exchange.Name, exchange.Type, exchange.Durable, exchange.AutoDelete,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            declaredExchanges.Add(exchange.Name);
        }

        foreach (RabbitMqRetryTopologyOptions retry in _options.Topology.RetryTopologies)
        {
            if (declaredExchanges.Add(retry.RetryExchange))
                await channel.ExchangeDeclareAsync(retry.RetryExchange, ExchangeType.Direct, durable: true, autoDelete: false, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (declaredExchanges.Add(retry.DeadLetterExchange))
                await channel.ExchangeDeclareAsync(retry.DeadLetterExchange, ExchangeType.Direct, durable: true, autoDelete: false, cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        var retriesBySource = _options.Topology.RetryTopologies.ToDictionary(static x => x.SourceQueue, StringComparer.Ordinal);
        foreach (RabbitMqQueueOptions queue in _options.Topology.Queues)
        {
            IDictionary<string, object?>? arguments = BuildQueueArguments(queue, retriesBySource.GetValueOrDefault(queue.Name));
            await channel.QueueDeclareAsync(queue.Name, queue.Durable, queue.Exclusive, queue.AutoDelete, arguments, cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        foreach (RabbitMqBindingOptions binding in _options.Topology.Bindings)
            await channel.QueueBindAsync(binding.Queue, binding.Exchange, binding.RoutingKey, cancellationToken: cancellationToken).ConfigureAwait(false);

        foreach (RabbitMqRetryTopologyOptions retry in _options.Topology.RetryTopologies)
        {
            var retryArguments = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["x-message-ttl"] = checked((long)retry.RetryDelay.TotalMilliseconds),
                ["x-dead-letter-exchange"] = retry.ReturnExchange,
                ["x-dead-letter-routing-key"] = retry.ReturnRoutingKey
            };
            await channel.QueueDeclareAsync(retry.RetryQueue, durable: true, exclusive: false, autoDelete: false,
                arguments: retryArguments, cancellationToken: cancellationToken).ConfigureAwait(false);
            await channel.QueueBindAsync(retry.RetryQueue, retry.RetryExchange, retry.RetryRoutingKey, cancellationToken: cancellationToken).ConfigureAwait(false);
            await channel.QueueDeclareAsync(retry.DeadLetterQueue, durable: true, exclusive: false, autoDelete: false,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            await channel.QueueBindAsync(retry.DeadLetterQueue, retry.DeadLetterExchange, retry.DeadLetterRoutingKey, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ValidateOnlyAsync(IChannel channel, CancellationToken cancellationToken)
    {
        var declaredExchanges = new HashSet<string>(StringComparer.Ordinal);
        foreach (RabbitMqExchangeOptions exchange in _options.Topology.Exchanges)
        {
            await channel.ExchangeDeclarePassiveAsync(exchange.Name, cancellationToken).ConfigureAwait(false);
            await channel.ExchangeDeclareAsync(exchange.Name, exchange.Type, exchange.Durable, exchange.AutoDelete,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            declaredExchanges.Add(exchange.Name);
        }

        foreach (RabbitMqRetryTopologyOptions retry in _options.Topology.RetryTopologies)
        {
            if (declaredExchanges.Add(retry.RetryExchange))
            {
                await channel.ExchangeDeclarePassiveAsync(retry.RetryExchange, cancellationToken).ConfigureAwait(false);
                await channel.ExchangeDeclareAsync(retry.RetryExchange, ExchangeType.Direct, durable: true, autoDelete: false,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            if (declaredExchanges.Add(retry.DeadLetterExchange))
            {
                await channel.ExchangeDeclarePassiveAsync(retry.DeadLetterExchange, cancellationToken).ConfigureAwait(false);
                await channel.ExchangeDeclareAsync(retry.DeadLetterExchange, ExchangeType.Direct, durable: true, autoDelete: false,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            }
        }

        var retriesBySource = _options.Topology.RetryTopologies.ToDictionary(static x => x.SourceQueue, StringComparer.Ordinal);
        foreach (RabbitMqQueueOptions queue in _options.Topology.Queues)
        {
            await channel.QueueDeclarePassiveAsync(queue.Name, cancellationToken).ConfigureAwait(false);
            IDictionary<string, object?>? arguments = BuildQueueArguments(queue, retriesBySource.GetValueOrDefault(queue.Name));
            await channel.QueueDeclareAsync(queue.Name, queue.Durable, queue.Exclusive, queue.AutoDelete, arguments,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        foreach (RabbitMqRetryTopologyOptions retry in _options.Topology.RetryTopologies)
        {
            var retryArguments = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["x-message-ttl"] = checked((long)retry.RetryDelay.TotalMilliseconds),
                ["x-dead-letter-exchange"] = retry.ReturnExchange,
                ["x-dead-letter-routing-key"] = retry.ReturnRoutingKey
            };
            await channel.QueueDeclarePassiveAsync(retry.RetryQueue, cancellationToken).ConfigureAwait(false);
            await channel.QueueDeclareAsync(retry.RetryQueue, durable: true, exclusive: false, autoDelete: false,
                arguments: retryArguments, cancellationToken: cancellationToken).ConfigureAwait(false);
            await channel.QueueDeclarePassiveAsync(retry.DeadLetterQueue, cancellationToken).ConfigureAwait(false);
            await channel.QueueDeclareAsync(retry.DeadLetterQueue, durable: true, exclusive: false, autoDelete: false,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        // AMQP 0-9-1 exposes no read-only binding enumeration. Binding references are validated locally,
        // while broker-side binding existence remains an operator/topology-management responsibility in ValidateOnly mode.
    }

    private static IDictionary<string, object?>? BuildQueueArguments(RabbitMqQueueOptions queue, RabbitMqRetryTopologyOptions? retry)
    {
        Dictionary<string, object?>? result = null;
        void Add(string key, object value) { result ??= new Dictionary<string, object?>(StringComparer.Ordinal); result[key] = value; }
        if (retry is not null)
        {
            Add("x-dead-letter-exchange", retry.RetryExchange);
            Add("x-dead-letter-routing-key", retry.RetryRoutingKey);
        }
        else if (queue.DeadLetterExchange is not null)
        {
            Add("x-dead-letter-exchange", queue.DeadLetterExchange);
            if (queue.DeadLetterRoutingKey is not null) Add("x-dead-letter-routing-key", queue.DeadLetterRoutingKey);
        }
        if (queue.SingleActiveConsumer) Add("x-single-active-consumer", true);
        return result;
    }
}
