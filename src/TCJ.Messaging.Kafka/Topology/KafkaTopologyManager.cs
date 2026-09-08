using Confluent.Kafka;
using Confluent.Kafka.Admin;
using TCJ.Messaging.Kafka.Configuration;
using TCJ.Messaging.Kafka.Publishing;

namespace TCJ.Messaging.Kafka.Topology;

internal sealed class KafkaTopologyManager
{
    private readonly TcjKafkaOptions _options;
    internal KafkaTopologyManager(TcjKafkaOptions options)=>_options=options;
    internal Task EnsureAsync(CancellationToken cancellationToken)
    {
        _options.Validate(); if(_options.TopologyMode==KafkaTopologyMode.Disabled)return Task.CompletedTask;
        using IAdminClient admin=new AdminClientBuilder(KafkaConfigFactory.Admin(_options)).Build();
        if(_options.TopologyMode==KafkaTopologyMode.ValidateOnly)
        {
            Metadata metadata=admin.GetMetadata(_options.PublishTimeout); var names=metadata.Topics.Select(static x=>x.Topic).ToHashSet(StringComparer.Ordinal);
            foreach(KafkaTopicOptions topic in _options.Topics) if(!names.Contains(topic.Name)) throw new InvalidOperationException($"Kafka topic '{topic.Name}' is missing in ValidateOnly mode.");
            return Task.CompletedTask;
        }
        return DeclareAsync(admin,cancellationToken);
    }
    private async Task DeclareAsync(IAdminClient admin,CancellationToken token)
    {
        if(_options.Topics.Count==0)return; Metadata metadata=admin.GetMetadata(_options.PublishTimeout); var existing=metadata.Topics.Select(static x=>x.Topic).ToHashSet(StringComparer.Ordinal);
        var missing=_options.Topics.Where(x=>!existing.Contains(x.Name)).Select(x=>new TopicSpecification{Name=x.Name,NumPartitions=x.Partitions,ReplicationFactor=x.ReplicationFactor}).ToArray();
        if(missing.Length==0)return; using var cts=CancellationTokenSource.CreateLinkedTokenSource(token); cts.CancelAfter(_options.PublishTimeout); await admin.CreateTopicsAsync(missing).WaitAsync(cts.Token).ConfigureAwait(false);
    }
}
