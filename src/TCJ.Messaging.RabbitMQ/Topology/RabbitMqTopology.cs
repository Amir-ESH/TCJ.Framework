using TCJ.Messaging.Envelopes;
using TCJ.Messaging.RabbitMQ.Configuration;

namespace TCJ.Messaging.RabbitMQ.Topology;

/// <summary>Controls whether the application declares, validates, or ignores RabbitMQ topology.</summary>
public enum RabbitMqTopologyMode { Declare = 0, ValidateOnly = 1, Disabled = 2 }

/// <summary>Explicit exchange declaration.</summary>
public sealed class RabbitMqExchangeOptions
{
    /// <summary>Exchange name.</summary>
    public required string Name { get; set; }
    /// <summary>AMQP exchange type: direct, topic, or fanout.</summary>
    public string Type { get; set; } = "topic";
    /// <summary>Whether the exchange survives broker restart.</summary>
    public bool Durable { get; set; } = true;
    /// <summary>Whether RabbitMQ automatically deletes the exchange when unused.</summary>
    public bool AutoDelete { get; set; }
}

/// <summary>Explicit queue declaration.</summary>
public sealed class RabbitMqQueueOptions
{
    /// <summary>Queue name.</summary>
    public required string Name { get; set; }
    /// <summary>Whether the queue survives broker restart.</summary>
    public bool Durable { get; set; } = true;
    /// <summary>Whether the queue is exclusive to the declaring connection.</summary>
    public bool Exclusive { get; set; }
    /// <summary>Whether RabbitMQ automatically deletes the queue.</summary>
    public bool AutoDelete { get; set; }
    /// <summary>Optional explicit dead-letter exchange when no TCJ retry topology owns this queue.</summary>
    public string? DeadLetterExchange { get; set; }
    /// <summary>Optional explicit dead-letter routing key.</summary>
    public string? DeadLetterRoutingKey { get; set; }
    /// <summary>Enables RabbitMQ single-active-consumer for this queue.</summary>
    public bool SingleActiveConsumer { get; set; }
}

/// <summary>Explicit exchange-to-queue binding declaration.</summary>
public sealed class RabbitMqBindingOptions
{
    /// <summary>Exchange name.</summary>
    public required string Exchange { get; set; }
    /// <summary>Queue name.</summary>
    public required string Queue { get; set; }
    /// <summary>Binding routing key or topic pattern.</summary>
    public required string RoutingKey { get; set; }
}

/// <summary>Finite broker-native delayed retry and terminal dead-letter topology for one source queue.</summary>
public sealed class RabbitMqRetryTopologyOptions
{
    /// <summary>Main source queue.</summary>
    public required string SourceQueue { get; set; }
    /// <summary>Exchange receiving rejected main-queue messages for delayed retry.</summary>
    public required string RetryExchange { get; set; }
    /// <summary>Queue holding retry deliveries until its bounded TTL expires.</summary>
    public required string RetryQueue { get; set; }
    /// <summary>Routing key from the main queue to the retry queue.</summary>
    public required string RetryRoutingKey { get; set; }
    /// <summary>Exchange to which the retry queue dead-letters after TTL.</summary>
    public required string ReturnExchange { get; set; }
    /// <summary>Routing key used when the retry TTL expires.</summary>
    public required string ReturnRoutingKey { get; set; }
    /// <summary>Terminal dead-letter exchange.</summary>
    public required string DeadLetterExchange { get; set; }
    /// <summary>Terminal dead-letter queue.</summary>
    public required string DeadLetterQueue { get; set; }
    /// <summary>Terminal dead-letter routing key.</summary>
    public required string DeadLetterRoutingKey { get; set; }
    /// <summary>Bounded delay implemented with a retry queue TTL.</summary>
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(5);
}

/// <summary>Collection of explicit RabbitMQ exchanges, queues, bindings, and finite retry paths.</summary>
public sealed class RabbitMqTopologyOptions
{
    /// <summary>Configured exchanges.</summary>
    public IList<RabbitMqExchangeOptions> Exchanges { get; } = new List<RabbitMqExchangeOptions>();
    /// <summary>Configured queues.</summary>
    public IList<RabbitMqQueueOptions> Queues { get; } = new List<RabbitMqQueueOptions>();
    /// <summary>Configured bindings.</summary>
    public IList<RabbitMqBindingOptions> Bindings { get; } = new List<RabbitMqBindingOptions>();
    /// <summary>Configured finite retry/dead-letter paths.</summary>
    public IList<RabbitMqRetryTopologyOptions> RetryTopologies { get; } = new List<RabbitMqRetryTopologyOptions>();

    internal void Validate(TcjRabbitMqOptions owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        var exchanges = new HashSet<string>(StringComparer.Ordinal);
        foreach (RabbitMqExchangeOptions exchange in Exchanges)
        {
            ArgumentNullException.ThrowIfNull(exchange);
            RabbitMqValidation.ValidateEntityName(exchange.Name, nameof(exchange.Name), false);
            RabbitMqValidation.ValidateExchangeType(exchange.Type, nameof(exchange.Type));
            if (!exchanges.Add(exchange.Name)) throw new ArgumentException($"Exchange '{exchange.Name}' is declared more than once.", nameof(Exchanges));
        }
        if (owner.TopologyMode == RabbitMqTopologyMode.Declare && !exchanges.Contains(owner.DefaultExchange))
            throw new ArgumentException($"DefaultExchange '{owner.DefaultExchange}' must be explicitly declared when TopologyMode is Declare.", nameof(owner.DefaultExchange));

        var queues = new HashSet<string>(StringComparer.Ordinal);
        foreach (RabbitMqQueueOptions queue in Queues)
        {
            ArgumentNullException.ThrowIfNull(queue);
            RabbitMqValidation.ValidateEntityName(queue.Name, nameof(queue.Name), false);
            if (!queues.Add(queue.Name)) throw new ArgumentException($"Queue '{queue.Name}' is declared more than once.", nameof(Queues));
            if (queue.Exclusive && queue.Durable) throw new ArgumentException($"Queue '{queue.Name}' cannot be both exclusive and durable.", nameof(Queues));
            if (queue.DeadLetterExchange is not null) RabbitMqValidation.ValidateEntityName(queue.DeadLetterExchange, nameof(queue.DeadLetterExchange), false);
            if (queue.DeadLetterRoutingKey is not null) RabbitMqValidation.ValidateRoutingKey(queue.DeadLetterRoutingKey, nameof(queue.DeadLetterRoutingKey), false);
        }

        var bindingKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (RabbitMqBindingOptions binding in Bindings)
        {
            ArgumentNullException.ThrowIfNull(binding);
            RabbitMqValidation.ValidateEntityName(binding.Exchange, nameof(binding.Exchange), false);
            RabbitMqValidation.ValidateEntityName(binding.Queue, nameof(binding.Queue), false);
            RabbitMqValidation.ValidateRoutingKey(binding.RoutingKey, nameof(binding.RoutingKey), true);
            if (owner.TopologyMode != RabbitMqTopologyMode.Disabled && (!exchanges.Contains(binding.Exchange) || !queues.Contains(binding.Queue)))
                throw new ArgumentException($"Binding '{binding.Exchange}->{binding.Queue}' references undeclared topology.", nameof(Bindings));
            if (!bindingKeys.Add($"{binding.Exchange}\0{binding.Queue}\0{binding.RoutingKey}"))
                throw new ArgumentException("Duplicate RabbitMQ binding declaration detected.", nameof(Bindings));
        }

        var retrySources = new HashSet<string>(StringComparer.Ordinal);
        foreach (RabbitMqRetryTopologyOptions retry in RetryTopologies)
        {
            ArgumentNullException.ThrowIfNull(retry);
            RabbitMqValidation.ValidateEntityName(retry.SourceQueue, nameof(retry.SourceQueue), false);
            RabbitMqValidation.ValidateEntityName(retry.RetryExchange, nameof(retry.RetryExchange), false);
            RabbitMqValidation.ValidateEntityName(retry.RetryQueue, nameof(retry.RetryQueue), false);
            RabbitMqValidation.ValidateRoutingKey(retry.RetryRoutingKey, nameof(retry.RetryRoutingKey), false);
            RabbitMqValidation.ValidateEntityName(retry.ReturnExchange, nameof(retry.ReturnExchange), false);
            RabbitMqValidation.ValidateRoutingKey(retry.ReturnRoutingKey, nameof(retry.ReturnRoutingKey), false);
            RabbitMqValidation.ValidateEntityName(retry.DeadLetterExchange, nameof(retry.DeadLetterExchange), false);
            RabbitMqValidation.ValidateEntityName(retry.DeadLetterQueue, nameof(retry.DeadLetterQueue), false);
            RabbitMqValidation.ValidateRoutingKey(retry.DeadLetterRoutingKey, nameof(retry.DeadLetterRoutingKey), false);
            RabbitMqValidation.ValidateTimeout(retry.RetryDelay, nameof(retry.RetryDelay), TimeSpan.FromHours(1));
            if (!retrySources.Add(retry.SourceQueue)) throw new ArgumentException($"Queue '{retry.SourceQueue}' has more than one retry topology.", nameof(RetryTopologies));
            if (retry.SourceQueue == retry.RetryQueue || retry.SourceQueue == retry.DeadLetterQueue || retry.RetryQueue == retry.DeadLetterQueue)
                throw new ArgumentException("Retry topology queues must be distinct and finite.", nameof(RetryTopologies));
            if (owner.TopologyMode != RabbitMqTopologyMode.Disabled && !queues.Contains(retry.SourceQueue))
                throw new ArgumentException($"Retry source queue '{retry.SourceQueue}' is not declared.", nameof(RetryTopologies));
        }
    }
}

/// <summary>Maps logical TCJ message types and versions to safe deterministic RabbitMQ routing keys.</summary>
public interface IRabbitMqRoutingKeyStrategy
{
    /// <summary>Returns the publish routing key for one logical message.</summary>
    string GetRoutingKey(string messageType, int messageVersion, TransportMessageEnvelope envelope);
}

/// <summary>Default routing strategy: a normalized logical message type followed by <c>.vN</c>.</summary>
public sealed class DefaultRabbitMqRoutingKeyStrategy : IRabbitMqRoutingKeyStrategy
{
    /// <inheritdoc />
    public string GetRoutingKey(string messageType, int messageVersion, TransportMessageEnvelope envelope)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(messageType);
        if (messageVersion <= 0) throw new ArgumentOutOfRangeException(nameof(messageVersion));
        ArgumentNullException.ThrowIfNull(envelope);
        string normalized = string.Join('.', messageType.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(static segment => new string(segment.ToLowerInvariant().Select(static c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-').ToArray())));
        string key = $"{normalized}.v{messageVersion}";
        RabbitMqValidation.ValidateRoutingKey(key, nameof(messageType), false);
        return key;
    }
}

/// <summary>Fluent helpers for explicit RabbitMQ topology declarations.</summary>
public sealed class RabbitMqTopologyBuilder
{
    internal RabbitMqTopologyBuilder(RabbitMqTopologyOptions topology) => Topology = topology;
    internal RabbitMqTopologyOptions Topology { get; }

    /// <summary>Adds a durable topic exchange.</summary>
    public RabbitMqTopologyBuilder AddTopicExchange(string name) => AddExchange(name, "topic");
    /// <summary>Adds a durable direct exchange.</summary>
    public RabbitMqTopologyBuilder AddDirectExchange(string name) => AddExchange(name, "direct");
    /// <summary>Adds a durable fanout exchange.</summary>
    public RabbitMqTopologyBuilder AddFanoutExchange(string name) => AddExchange(name, "fanout");
    /// <summary>Adds a durable non-exclusive non-auto-delete queue.</summary>
    public RabbitMqTopologyBuilder AddQueue(string name) { Topology.Queues.Add(new RabbitMqQueueOptions { Name = name }); return this; }
    /// <summary>Adds an exchange-to-queue binding.</summary>
    public RabbitMqTopologyBuilder Bind(string exchange, string queue, string routingKey) { Topology.Bindings.Add(new RabbitMqBindingOptions { Exchange = exchange, Queue = queue, RoutingKey = routingKey }); return this; }
    /// <summary>Adds a finite retry path backed by a TTL queue and terminal dead-letter exchange/queue.</summary>
    public RabbitMqTopologyBuilder AddRetryTopology(RabbitMqRetryTopologyOptions options) { ArgumentNullException.ThrowIfNull(options); Topology.RetryTopologies.Add(options); return this; }
    private RabbitMqTopologyBuilder AddExchange(string name, string type) { Topology.Exchanges.Add(new RabbitMqExchangeOptions { Name = name, Type = type }); return this; }
}
