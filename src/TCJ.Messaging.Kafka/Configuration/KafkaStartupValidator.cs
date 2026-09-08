using Confluent.Kafka;
using TCJ.Messaging.Configuration;
using TCJ.Messaging.Kafka.Topology;

namespace TCJ.Messaging.Kafka.Configuration;

internal sealed class KafkaStartupValidator : IMessagingStartupValidator
{
    private readonly MessagingStartupValidator _neutral; private readonly KafkaTopologyManager _topology; private readonly TcjKafkaOptions _kafka; private readonly SemaphoreSlim _gate=new(1,1); private volatile bool _validated;
    internal KafkaStartupValidator(MessagingStartupValidator neutral,KafkaTopologyManager topology,TcjKafkaOptions kafka){_neutral=neutral;_topology=topology;_kafka=kafka;}
    public async Task ValidateAsync(CancellationToken cancellationToken=default)
    {
        if(_validated)return; await _gate.WaitAsync(cancellationToken).ConfigureAwait(false); try{if(_validated)return;_kafka.Validate();await _neutral.ValidateAsync(cancellationToken).ConfigureAwait(false);try{await _topology.EnsureAsync(cancellationToken).ConfigureAwait(false);}catch(KafkaException e){throw new InvalidOperationException(e.Error.IsFatal?"Kafka startup validation failed with a permanent broker error.":"Kafka startup validation failed with a transient broker error.");}_validated=true;}finally{_gate.Release();}
    }
}
