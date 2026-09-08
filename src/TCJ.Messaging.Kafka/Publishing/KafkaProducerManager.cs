using Confluent.Kafka;
using TCJ.Messaging.Kafka.Configuration;

namespace TCJ.Messaging.Kafka.Publishing;

internal sealed class KafkaProducerManager : IAsyncDisposable
{
    private readonly TcjKafkaOptions _options; private readonly SemaphoreSlim _gate = new(1,1); private IProducer<string,byte[]>? _producer; private bool _disposed;
    internal KafkaProducerManager(TcjKafkaOptions options) => _options = options;
    internal async ValueTask<IProducer<string,byte[]>> GetAsync(CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(_disposed,this); if (_producer is not null) return _producer; await _gate.WaitAsync(token).ConfigureAwait(false);
        try { if (_producer is not null) return _producer; _options.Validate(); _producer = new ProducerBuilder<string,byte[]>(KafkaConfigFactory.Producer(_options)).Build(); return _producer; }
        finally { _gate.Release(); }
    }
    public async ValueTask DisposeAsync() { if (_disposed) return; _disposed=true; await _gate.WaitAsync().ConfigureAwait(false); try { if (_producer is not null) { try { _producer.Flush(_options.ShutdownTimeout); } catch { } _producer.Dispose(); _producer=null; } } finally { _gate.Release(); _gate.Dispose(); } }
}

internal static class KafkaConfigFactory
{
    internal static ProducerConfig Producer(TcjKafkaOptions o) => new()
    {
        BootstrapServers=o.BootstrapServers, ClientId=o.ClientId, EnableIdempotence=true, Acks=Acks.All,
        MessageSendMaxRetries=o.ProducerRetryCount, RetryBackoffMs=(int)o.ProducerRetryBackoff.TotalMilliseconds,
        MessageTimeoutMs=(int)o.PublishTimeout.TotalMilliseconds, MaxInFlight=5,
        SecurityProtocol=Security(o.SecurityMode), SaslMechanism=Sasl(o.SaslMechanism), SaslUsername=o.SaslUsername, SaslPassword=o.SaslPassword, SslCaLocation=o.SslCaLocation
    };
    internal static ConsumerConfig Consumer(TcjKafkaOptions o,string group) => new()
    {
        BootstrapServers=o.BootstrapServers, ClientId=o.ClientId, GroupId=group, EnableAutoCommit=false, EnableAutoOffsetStore=false,
        SessionTimeoutMs=(int)o.SessionTimeout.TotalMilliseconds, MaxPollIntervalMs=(int)o.MaxPollInterval.TotalMilliseconds,
        AutoOffsetReset=o.AutoOffsetReset switch { KafkaOffsetResetMode.Earliest=>Confluent.Kafka.AutoOffsetReset.Earliest, KafkaOffsetResetMode.Latest=>Confluent.Kafka.AutoOffsetReset.Latest, _=>Confluent.Kafka.AutoOffsetReset.Error },
        SecurityProtocol=Security(o.SecurityMode), SaslMechanism=Sasl(o.SaslMechanism), SaslUsername=o.SaslUsername, SaslPassword=o.SaslPassword, SslCaLocation=o.SslCaLocation
    };
    internal static AdminClientConfig Admin(TcjKafkaOptions o) => new() { BootstrapServers=o.BootstrapServers, ClientId=o.ClientId, SecurityProtocol=Security(o.SecurityMode), SaslMechanism=Sasl(o.SaslMechanism), SaslUsername=o.SaslUsername, SaslPassword=o.SaslPassword, SslCaLocation=o.SslCaLocation };
    private static SecurityProtocol Security(KafkaSecurityMode v)=>v switch { KafkaSecurityMode.Tls=>SecurityProtocol.Ssl, KafkaSecurityMode.SaslPlaintext=>SecurityProtocol.SaslPlaintext, KafkaSecurityMode.SaslTls=>SecurityProtocol.SaslSsl, _=>SecurityProtocol.Plaintext };
    private static SaslMechanism Sasl(KafkaSaslMechanism v)=>v switch { KafkaSaslMechanism.ScramSha256=>Confluent.Kafka.SaslMechanism.ScramSha256, KafkaSaslMechanism.ScramSha512=>Confluent.Kafka.SaslMechanism.ScramSha512, _=>Confluent.Kafka.SaslMechanism.Plain };
}
