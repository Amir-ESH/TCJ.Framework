namespace TCJ.Messaging.Kafka.Configuration;

/// <summary>Kafka acknowledgement policy exposed without Kafka SDK types.</summary>
public enum KafkaAcknowledgementMode { /// <summary>All in-sync replicas must acknowledge.</summary>
    All = 0 }
/// <summary>Kafka security protocol exposed without Kafka SDK types.</summary>
public enum KafkaSecurityMode { /// <summary>Unencrypted connection.</summary>
    Plaintext = 0, /// <summary>TLS.</summary>
    Tls = 1, /// <summary>SASL over plaintext.</summary>
    SaslPlaintext = 2, /// <summary>SASL over TLS.</summary>
    SaslTls = 3 }
/// <summary>Kafka SASL mechanism exposed without Kafka SDK types.</summary>
public enum KafkaSaslMechanism { /// <summary>PLAIN.</summary>
    Plain = 0, /// <summary>SCRAM-SHA-256.</summary>
    ScramSha256 = 1, /// <summary>SCRAM-SHA-512.</summary>
    ScramSha512 = 2 }
/// <summary>Kafka offset reset policy.</summary>
public enum KafkaOffsetResetMode { /// <summary>Fail if no offset exists.</summary>
    Error = 0, /// <summary>Start at earliest available record.</summary>
    Earliest = 1, /// <summary>Start at latest position.</summary>
    Latest = 2 }
/// <summary>Kafka topology ownership mode.</summary>
public enum KafkaTopologyMode { /// <summary>Create configured topics when absent.</summary>
    Declare = 0, /// <summary>Validate configured topics without mutation.</summary>
    ValidateOnly = 1, /// <summary>Do not require admin permissions.</summary>
    Disabled = 2 }

/// <summary>Configured Kafka topic declaration.</summary>
public sealed class KafkaTopicOptions
{
    /// <summary>Topic name.</summary>
    public required string Name { get; init; }
    /// <summary>Partition count.</summary>
    public int Partitions { get; init; } = 1;
    /// <summary>Replication factor.</summary>
    public short ReplicationFactor { get; init; } = 1;
}

/// <summary>Configures the TCJ Kafka transport adapter.</summary>
public sealed class TcjKafkaOptions
{
    /// <summary>Comma-separated Kafka bootstrap endpoints.</summary>
    public string BootstrapServers { get; set; } = string.Empty;
    /// <summary>Bounded Kafka client identifier.</summary>
    public string ClientId { get; set; } = "tcj-messaging";
    /// <summary>Disables automatic offset commit. Must remain false.</summary>
    public bool EnableAutoCommit { get; set; }
    /// <summary>Disables automatic offset store. Must remain false.</summary>
    public bool EnableAutoOffsetStore { get; set; }
    /// <summary>Enables Kafka producer idempotence. Must remain true.</summary>
    public bool EnableIdempotence { get; set; } = true;
    /// <summary>Strong producer acknowledgement mode. Must remain All.</summary>
    public KafkaAcknowledgementMode AcknowledgementMode { get; set; } = KafkaAcknowledgementMode.All;
    /// <summary>Maximum Kafka-client broker-operation retries.</summary>
    public int ProducerRetryCount { get; set; } = 3;
    /// <summary>Backoff between Kafka-client producer retries.</summary>
    public TimeSpan ProducerRetryBackoff { get; set; } = TimeSpan.FromMilliseconds(250);
    /// <summary>Bounded publish timeout.</summary>
    public TimeSpan PublishTimeout { get; set; } = TimeSpan.FromSeconds(30);
    /// <summary>Bounded shutdown timeout.</summary>
    public TimeSpan ShutdownTimeout { get; set; } = TimeSpan.FromSeconds(30);
    /// <summary>Kafka consumer session timeout.</summary>
    public TimeSpan SessionTimeout { get; set; } = TimeSpan.FromSeconds(30);
    /// <summary>Kafka maximum poll interval.</summary>
    public TimeSpan MaxPollInterval { get; set; } = TimeSpan.FromMinutes(5);
    /// <summary>Maximum concurrently processed partitions.</summary>
    public int MaximumConcurrentPartitions { get; set; } = 8;
    /// <summary>Maximum total buffered/in-flight deliveries.</summary>
    public int MaximumBufferedMessages { get; set; } = 64;
    /// <summary>Maximum batch size accepted by the adapter.</summary>
    public int MaximumBatchSize { get; set; } = 256;
    /// <summary>Maximum distinct partitions tracked by one runner.</summary>
    public int MaximumTrackedPartitions { get; set; } = 1024;
    /// <summary>Maximum processing attempts before terminal dead-letter.</summary>
    public int MaximumProcessingAttempts { get; set; } = 5;
    /// <summary>Default topic used when PublishContext does not specify one.</summary>
    public string DefaultTopic { get; set; } = "tcj.events";
    /// <summary>Suffix used for immediate durable retry topics.</summary>
    public string RetryTopicSuffix { get; set; } = ".retry";
    /// <summary>Suffix used for dead-letter topics.</summary>
    public string DeadLetterTopicSuffix { get; set; } = ".dead";
    /// <summary>Offset reset policy when a group has no committed offset.</summary>
    public KafkaOffsetResetMode AutoOffsetReset { get; set; } = KafkaOffsetResetMode.Error;
    /// <summary>Topology ownership mode.</summary>
    public KafkaTopologyMode TopologyMode { get; set; } = KafkaTopologyMode.Disabled;
    /// <summary>Security protocol.</summary>
    public KafkaSecurityMode SecurityMode { get; set; } = KafkaSecurityMode.Plaintext;
    /// <summary>SASL mechanism.</summary>
    public KafkaSaslMechanism SaslMechanism { get; set; } = KafkaSaslMechanism.Plain;
    /// <summary>SASL username. Never emitted to diagnostics.</summary>
    public string? SaslUsername { get; set; }
    /// <summary>SASL password. Never emitted to diagnostics.</summary>
    public string? SaslPassword { get; set; }
    /// <summary>Optional CA certificate file path.</summary>
    public string? SslCaLocation { get; set; }
    /// <summary>Explicit topics used by Declare/ValidateOnly modes.</summary>
    public IList<KafkaTopicOptions> Topics { get; } = new List<KafkaTopicOptions>();

    internal void Validate()
    {
        Require(BootstrapServers, nameof(BootstrapServers), 2048);
        foreach (string endpoint in BootstrapServers.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int colon = endpoint.LastIndexOf(':');
            if (colon <= 0 || colon == endpoint.Length - 1 || !int.TryParse(endpoint[(colon + 1)..], out int port) || port is <= 0 or > 65535)
                throw new ArgumentException("BootstrapServers must contain host:port endpoints.", nameof(BootstrapServers));
        }
        Require(ClientId, nameof(ClientId), 255);
        if (EnableAutoCommit) throw new ArgumentException("EnableAutoCommit must remain false.", nameof(EnableAutoCommit));
        if (EnableAutoOffsetStore) throw new ArgumentException("EnableAutoOffsetStore must remain false.", nameof(EnableAutoOffsetStore));
        if (!EnableIdempotence) throw new ArgumentException("EnableIdempotence must remain true.", nameof(EnableIdempotence));
        if (AcknowledgementMode != KafkaAcknowledgementMode.All) throw new ArgumentException("AcknowledgementMode must remain All.", nameof(AcknowledgementMode));
        if (ProducerRetryCount is < 1 or > 20) throw new ArgumentOutOfRangeException(nameof(ProducerRetryCount));
        ValidateTimeout(ProducerRetryBackoff, nameof(ProducerRetryBackoff), TimeSpan.FromSeconds(30), allowZero: ProducerRetryCount == 0);
        ValidateTimeout(PublishTimeout, nameof(PublishTimeout), TimeSpan.FromSeconds(120));
        ValidateTimeout(ShutdownTimeout, nameof(ShutdownTimeout), TimeSpan.FromMinutes(2));
        ValidateTimeout(SessionTimeout, nameof(SessionTimeout), TimeSpan.FromMinutes(5));
        ValidateTimeout(MaxPollInterval, nameof(MaxPollInterval), TimeSpan.FromMinutes(30));
        if (MaxPollInterval <= SessionTimeout) throw new ArgumentException("MaxPollInterval must be greater than SessionTimeout.", nameof(MaxPollInterval));
        if (MaximumConcurrentPartitions is < 1 or > 256) throw new ArgumentOutOfRangeException(nameof(MaximumConcurrentPartitions));
        if (MaximumBufferedMessages is < 1 or > 4096) throw new ArgumentOutOfRangeException(nameof(MaximumBufferedMessages));
        if (MaximumBatchSize is < 1 or > 4096) throw new ArgumentOutOfRangeException(nameof(MaximumBatchSize));
        if (MaximumTrackedPartitions is < 1 or > 16384) throw new ArgumentOutOfRangeException(nameof(MaximumTrackedPartitions));
        if (MaximumProcessingAttempts is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(MaximumProcessingAttempts));
        ValidateTopic(DefaultTopic, nameof(DefaultTopic));
        ValidateSuffix(RetryTopicSuffix, nameof(RetryTopicSuffix));
        ValidateSuffix(DeadLetterTopicSuffix, nameof(DeadLetterTopicSuffix));
        if (!Enum.IsDefined(AutoOffsetReset)) throw new ArgumentOutOfRangeException(nameof(AutoOffsetReset));
        if (!Enum.IsDefined(TopologyMode)) throw new ArgumentOutOfRangeException(nameof(TopologyMode));
        if (!Enum.IsDefined(SecurityMode)) throw new ArgumentOutOfRangeException(nameof(SecurityMode));
        if (!Enum.IsDefined(SaslMechanism)) throw new ArgumentOutOfRangeException(nameof(SaslMechanism));
        bool sasl = SecurityMode is KafkaSecurityMode.SaslPlaintext or KafkaSecurityMode.SaslTls;
        if (sasl) { Require(SaslUsername, nameof(SaslUsername), 512); Require(SaslPassword, nameof(SaslPassword), 4096); }
        else if (SaslUsername is not null || SaslPassword is not null) throw new ArgumentException("SASL credentials require a SASL security mode.");
        if (SslCaLocation is not null && SecurityMode is KafkaSecurityMode.Plaintext or KafkaSecurityMode.SaslPlaintext)
            throw new ArgumentException("SslCaLocation requires TLS.", nameof(SslCaLocation));
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (KafkaTopicOptions topic in Topics)
        {
            ValidateTopic(topic.Name, nameof(Topics));
            if (!names.Add(topic.Name)) throw new ArgumentException($"Duplicate Kafka topic '{topic.Name}'.", nameof(Topics));
            if (topic.Partitions is < 1 or > 10000) throw new ArgumentOutOfRangeException(nameof(Topics), "Topic partitions must be between 1 and 10000.");
            if (topic.ReplicationFactor is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(Topics), "ReplicationFactor must be between 1 and 100.");
        }
    }

    internal static void ValidateTopic(string value, string parameterName)
    {
        Require(value, parameterName, 249);
        if (value is "." or ".." || value.Any(static c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-')))
            throw new ArgumentException("Kafka topic names may contain only ASCII letters, digits, '.', '_' and '-'.", parameterName);
    }
    private static void ValidateSuffix(string value, string name) { Require(value, name, 64); if (!value.StartsWith('.', StringComparison.Ordinal)) throw new ArgumentException("Kafka topic suffixes must start with '.'.", name); ValidateTopic("x" + value, name); }
    private static void ValidateTimeout(TimeSpan value, string name, TimeSpan max, bool allowZero = false) { if ((allowZero ? value < TimeSpan.Zero : value <= TimeSpan.Zero) || value > max) throw new ArgumentOutOfRangeException(name); }
    private static void Require(string? value, string name, int max) { ArgumentException.ThrowIfNullOrWhiteSpace(value, name); if (value.Length > max || value.Any(char.IsControl)) throw new ArgumentException($"{name} is invalid or too long.", name); }
}
