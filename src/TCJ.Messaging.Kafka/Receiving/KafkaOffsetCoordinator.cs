using Confluent.Kafka;

namespace TCJ.Messaging.Kafka.Receiving;

internal sealed class KafkaStaleSettlementException : InvalidOperationException
{
    internal KafkaStaleSettlementException() : base("Kafka settlement is stale after partition revocation.") { }
}

internal sealed class KafkaOffsetCoordinator
{
    private sealed class PartitionState
    {
        internal PartitionState(long firstOffset,long generation){ NextOffset=firstOffset; Generation=generation; }
        internal long NextOffset; internal long Generation; internal bool Active=true; internal SortedSet<long> Seen=new(); internal HashSet<long> Completed=new();
    }
    private readonly Dictionary<TopicPartition,PartitionState> _states=new(); private readonly object _sync=new(); private long _generation;
    internal long Assign(TopicPartition partition,long? firstOffset=null){ lock(_sync){ long g=++_generation; _states[partition]=new PartitionState(firstOffset ?? long.MaxValue,g); return g; } }
    internal long Register(TopicPartition partition,long offset)
    {
        lock(_sync){ if(!_states.TryGetValue(partition,out PartitionState? s)||!s.Active) throw new InvalidOperationException("Kafka partition is not assigned."); if(s.NextOffset==long.MaxValue) s.NextOffset=offset; if(offset<s.NextOffset) return s.Generation; s.Seen.Add(offset); return s.Generation; }
    }
    internal Offset? Complete(TopicPartition partition,long offset,long generation)
    {
        lock(_sync){ if(!_states.TryGetValue(partition,out PartitionState? s)||!s.Active||s.Generation!=generation) throw new KafkaStaleSettlementException(); if(offset<s.NextOffset) return null; if(!s.Seen.Contains(offset)) throw new InvalidOperationException("Kafka offset was not registered before settlement."); s.Completed.Add(offset); long start=s.NextOffset; while(s.Seen.Contains(s.NextOffset)&&s.Completed.Contains(s.NextOffset)){ s.Seen.Remove(s.NextOffset); s.Completed.Remove(s.NextOffset); s.NextOffset++; } return s.NextOffset>start?new Offset(s.NextOffset):null; }
    }
    internal TopicPartitionOffset? SafePosition(TopicPartition partition)
    { lock(_sync){ if(!_states.TryGetValue(partition,out PartitionState? s)||s.NextOffset==long.MaxValue) return null; return new TopicPartitionOffset(partition,new Offset(s.NextOffset)); } }
    internal void Revoke(TopicPartition partition){ lock(_sync){ if(_states.TryGetValue(partition,out PartitionState? s)) s.Active=false; } }
    internal void Remove(TopicPartition partition){ lock(_sync){ _states.Remove(partition); } }
}
