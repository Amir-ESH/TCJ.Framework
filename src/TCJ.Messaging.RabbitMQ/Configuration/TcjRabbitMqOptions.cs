using TCJ.Messaging.RabbitMQ.Topology;

namespace TCJ.Messaging.RabbitMQ.Configuration;

/// <summary>Configures the production RabbitMQ transport adapter.</summary>
public sealed class TcjRabbitMqOptions
{
    /// <summary>RabbitMQ host name.</summary>
    public string HostName { get; set; } = "localhost";
    /// <summary>AMQP TCP port.</summary>
    public int Port { get; set; } = 5672;
    /// <summary>RabbitMQ virtual host.</summary>
    public string VirtualHost { get; set; } = "/";
    /// <summary>RabbitMQ user name. Prefer secret-provider or environment configuration in production.</summary>
    public string UserName { get; set; } = "guest";
    /// <summary>RabbitMQ password. The adapter never includes this value in diagnostics.</summary>
    public string Password { get; set; } = "guest";
    /// <summary>Enables TLS for the AMQP connection.</summary>
    public bool UseTls { get; set; }
    /// <summary>Optional TLS server name. Defaults to <see cref="HostName"/> when TLS is enabled.</summary>
    public string? TlsServerName { get; set; }
    /// <summary>Optional bounded client-provided connection name.</summary>
    public string? ClientProvidedName { get; set; }
    /// <summary>Maximum unacknowledged deliveries requested from RabbitMQ.</summary>
    public ushort PrefetchCount { get; set; } = 16;
    /// <summary>Maximum concurrently processed deliveries.</summary>
    public int MaximumConcurrentMessages { get; set; } = 8;
    /// <summary>Bounded connection establishment timeout.</summary>
    public TimeSpan ConnectionTimeout { get; set; } = TimeSpan.FromSeconds(10);
    /// <summary>Bounded publisher-confirm timeout.</summary>
    public TimeSpan PublishConfirmTimeout { get; set; } = TimeSpan.FromSeconds(10);
    /// <summary>Bounded graceful-shutdown timeout.</summary>
    public TimeSpan ShutdownTimeout { get; set; } = TimeSpan.FromSeconds(30);
    /// <summary>Delay between RabbitMQ client automatic-recovery attempts.</summary>
    public TimeSpan NetworkRecoveryInterval { get; set; } = TimeSpan.FromSeconds(5);
    /// <summary>Enables RabbitMQ automatic connection recovery.</summary>
    public bool AutomaticRecoveryEnabled { get; set; } = true;
    /// <summary>Enables RabbitMQ client topology recovery for client-created entities.</summary>
    public bool TopologyRecoveryEnabled { get; set; } = true;
    /// <summary>Requires routing for published messages and classifies basic.return as a permanent topology failure.</summary>
    public bool MandatoryPublish { get; set; } = true;
    /// <summary>Exchange used when the transport-neutral publish context does not specify a destination.</summary>
    public string DefaultExchange { get; set; } = "tcj.events";
    /// <summary>Maximum delivery attempts allowed before adapter-managed retry settlement dead-letters the delivery.</summary>
    public int MaximumProcessingAttempts { get; set; } = 5;
    /// <summary>Explicit topology ownership/declaration mode.</summary>
    public RabbitMqTopologyMode TopologyMode { get; set; } = RabbitMqTopologyMode.Declare;
    /// <summary>Explicit RabbitMQ topology.</summary>
    public RabbitMqTopologyOptions Topology { get; } = new();

    internal void Validate()
    {
        RabbitMqValidation.RequireNonEmpty(HostName, nameof(HostName), 255);
        if (Port is <= 0 or > 65535) throw new ArgumentOutOfRangeException(nameof(Port), "Port must be between 1 and 65535.");
        RabbitMqValidation.RequireNonEmpty(VirtualHost, nameof(VirtualHost), 255);
        RabbitMqValidation.RequireNonEmpty(UserName, nameof(UserName), 255);
        RabbitMqValidation.RequireNonEmpty(Password, nameof(Password), 1024);
        if (ClientProvidedName is not null) RabbitMqValidation.RequireNonEmpty(ClientProvidedName, nameof(ClientProvidedName), 255);
        if (UseTls && TlsServerName is not null) RabbitMqValidation.RequireNonEmpty(TlsServerName, nameof(TlsServerName), 255);
        if (PrefetchCount is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(PrefetchCount), "PrefetchCount must be between 1 and 1000.");
        if (MaximumConcurrentMessages is < 1 or > 256) throw new ArgumentOutOfRangeException(nameof(MaximumConcurrentMessages), "MaximumConcurrentMessages must be between 1 and 256.");
        if (MaximumConcurrentMessages > PrefetchCount) throw new ArgumentException("MaximumConcurrentMessages cannot exceed PrefetchCount.", nameof(MaximumConcurrentMessages));
        RabbitMqValidation.ValidateTimeout(ConnectionTimeout, nameof(ConnectionTimeout), TimeSpan.FromSeconds(120));
        RabbitMqValidation.ValidateTimeout(PublishConfirmTimeout, nameof(PublishConfirmTimeout), TimeSpan.FromSeconds(120));
        RabbitMqValidation.ValidateTimeout(ShutdownTimeout, nameof(ShutdownTimeout), TimeSpan.FromMinutes(2));
        RabbitMqValidation.ValidateTimeout(NetworkRecoveryInterval, nameof(NetworkRecoveryInterval), TimeSpan.FromMinutes(1));
        if (MaximumProcessingAttempts is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(MaximumProcessingAttempts), "MaximumProcessingAttempts must be between 1 and 100.");
        RabbitMqValidation.ValidateEntityName(DefaultExchange, nameof(DefaultExchange), allowEmpty: false);
        if (!Enum.IsDefined(TopologyMode)) throw new ArgumentOutOfRangeException(nameof(TopologyMode));
        Topology.Validate(this);
    }
}

internal static class RabbitMqValidation
{
    private static readonly HashSet<string> ExchangeTypes = new(StringComparer.Ordinal) { "direct", "topic", "fanout" };

    internal static void RequireNonEmpty(string value, string parameterName, int maximumLength)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Length > maximumLength || value.Any(char.IsControl))
            throw new ArgumentException($"{parameterName} must be {maximumLength} characters or fewer and contain no control characters.", parameterName);
    }

    internal static void ValidateTimeout(TimeSpan value, string parameterName, TimeSpan maximum)
    {
        if (value <= TimeSpan.Zero || value > maximum)
            throw new ArgumentOutOfRangeException(parameterName, $"{parameterName} must be greater than zero and no greater than {maximum}.");
    }

    internal static void ValidateEntityName(string value, string parameterName, bool allowEmpty)
    {
        if (allowEmpty && value.Length == 0) return;
        RequireNonEmpty(value, parameterName, 128);
        if (value.StartsWith("amq.", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Names in the reserved 'amq.' namespace are not allowed.", parameterName);
        if (value.Any(static c => char.IsWhiteSpace(c) || c is '\0' or '\r' or '\n'))
            throw new ArgumentException("RabbitMQ entity names cannot contain whitespace or control characters.", parameterName);
    }

    internal static void ValidateRoutingKey(string value, string parameterName, bool bindingPattern)
    {
        RequireNonEmpty(value, parameterName, 254);
        if (System.Text.Encoding.UTF8.GetByteCount(value) > 254)
            throw new ArgumentException("RabbitMQ routing keys must be shorter than 255 UTF-8 bytes.", parameterName);
        if (value.Any(static c => char.IsControl(c) || char.IsWhiteSpace(c)))
            throw new ArgumentException("RabbitMQ routing keys cannot contain whitespace or control characters.", parameterName);
        if (!bindingPattern && (value.Contains('*') || value.Contains('#')))
            throw new ArgumentException("Published routing keys cannot contain topic wildcards.", parameterName);
    }

    internal static void ValidateExchangeType(string value, string parameterName)
    {
        if (!ExchangeTypes.Contains(value)) throw new ArgumentException("Exchange type must be direct, topic, or fanout.", parameterName);
    }
}
