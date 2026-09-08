using Confluent.Kafka;
using TCJ.Messaging.Kafka.Configuration;

namespace TCJ.Messaging.Kafka.Connections;

internal static class KafkaClientConfiguration
{
    internal static ClientConfig Common(TcjKafkaOptions options) => new()
    {
        BootstrapServers = options.BootstrapServers, ClientId = options.ClientId,
        SocketTimeoutMs = (int)options.HealthTimeout.TotalMilliseconds,
        SocketConnectionSetupTimeoutMs = (int)options.HealthTimeout.TotalMilliseconds,
        LogConnectionClose = false,
        SecurityProtocol = options.SecurityProtocol switch
        {
            KafkaSecurityProtocol.Tls => SecurityProtocol.Ssl,
            KafkaSecurityProtocol.SaslTls => SecurityProtocol.SaslSsl,
            _ => SecurityProtocol.Plaintext
        },
        SaslMechanism = options.SaslMechanism switch
        {
            KafkaSaslMechanism.Plain => SaslMechanism.Plain,
            KafkaSaslMechanism.ScramSha256 => SaslMechanism.ScramSha256,
            _ => SaslMechanism.ScramSha512
        },
        SaslUsername = options.SaslUsername, SaslPassword = options.SaslPassword,
        SslCaLocation = options.CertificateAuthorityLocation,
        SslCertificateLocation = options.ClientCertificateLocation,
        SslKeyLocation = options.ClientKeyLocation, SslKeyPassword = options.ClientKeyPassword,
        EnableSslCertificateVerification = true, SslEndpointIdentificationAlgorithm = SslEndpointIdentificationAlgorithm.Https
    };

    internal static ProducerConfig Producer(TcjKafkaOptions options) => new(Common(options))
    {
        EnableIdempotence = true, Acks = Acks.All, MessageSendMaxRetries = options.ProducerRetryCount,
        MessageTimeoutMs = (int)options.PublishTimeout.TotalMilliseconds,
        RequestTimeoutMs = (int)Math.Min(options.PublishTimeout.TotalMilliseconds, 10000),
        QueueBufferingMaxMessages = options.MaximumInFlightPublishes,
        QueueBufferingMaxKbytes = 16384, MaxInFlight = 5, RetryBackoffMs = 100,
        Partitioner = Partitioner.Murmur2Random
    };

    internal static ConsumerConfig Consumer(TcjKafkaOptions options, string group) => new(Common(options))
    {
        GroupId = group, EnableAutoCommit = false, EnableAutoOffsetStore = false,
        AutoOffsetReset = options.AutoOffsetReset switch
        {
            KafkaOffsetReset.Latest => AutoOffsetReset.Latest,
            KafkaOffsetReset.Error => AutoOffsetReset.Error,
            _ => AutoOffsetReset.Earliest
        },
        SessionTimeoutMs = (int)options.SessionTimeout.TotalMilliseconds,
        MaxPollIntervalMs = (int)options.MaxPollInterval.TotalMilliseconds,
        HeartbeatIntervalMs = (int)options.SessionTimeout.TotalMilliseconds / 3,
        AllowAutoCreateTopics = false,
        QueuedMinMessages = 1, QueuedMaxMessagesKbytes = 16384,
        FetchMaxBytes = 16777216, MaxPartitionFetchBytes = 1048576,
        PartitionAssignmentStrategy = PartitionAssignmentStrategy.Range,
        EnablePartitionEof = false
    };
}
