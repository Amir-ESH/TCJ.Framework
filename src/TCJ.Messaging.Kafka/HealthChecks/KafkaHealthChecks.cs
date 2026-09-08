using Confluent.Kafka;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using TCJ.Messaging.HealthChecks;
using TCJ.Messaging.Kafka.Configuration;
using TCJ.Messaging.Kafka.Publishing;

namespace TCJ.Messaging.Kafka.HealthChecks;

/// <summary>Stable Kafka health-check names.</summary>
public static class TcjKafkaHealthCheckNames
{
    /// <summary>Kafka connectivity readiness.</summary>
    public const string Transport="tcj.kafka.transport";
}
internal sealed class KafkaTransportHealthProbe : IMessagingTransportHealthProbe
{
    private readonly TcjKafkaOptions _options; internal KafkaTransportHealthProbe(TcjKafkaOptions options)=>_options=options;
    public ValueTask<bool> IsReadyAsync(CancellationToken cancellationToken=default){cancellationToken.ThrowIfCancellationRequested();try{_options.Validate();using IAdminClient admin=new AdminClientBuilder(KafkaConfigFactory.Admin(_options)).Build();Metadata m=admin.GetMetadata(_options.PublishTimeout);return ValueTask.FromResult(m.Brokers.Count>0&&!m.Brokers.All(static x=>x.BrokerId<0));}catch{ return ValueTask.FromResult(false);}}
}
internal sealed class KafkaReadinessHealthCheck : IHealthCheck
{
    private readonly KafkaTransportHealthProbe _probe; internal KafkaReadinessHealthCheck(KafkaTransportHealthProbe probe)=>_probe=probe;
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context,CancellationToken cancellationToken=default)=>await _probe.IsReadyAsync(cancellationToken).ConfigureAwait(false)?HealthCheckResult.Healthy("Kafka transport is ready."):HealthCheckResult.Unhealthy("Kafka transport is not ready.");
}
