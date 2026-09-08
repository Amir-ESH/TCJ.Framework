using Confluent.Kafka;
using TCJ.Messaging.Kafka.Receiving;
namespace TCJ.Messaging.Kafka.Tests;

public sealed class KafkaOffsetCoordinatorTests
{
    [Fact] public void Contiguous_completed_offsets_advance_to_next_position(){var c=new KafkaOffsetCoordinator();var p=new TopicPartition("t",0);long g=c.Assign(p);for(long i=10;i<=12;i++)c.Register(p,i);Assert.Equal(11,c.Complete(p,10,g)!.Value.Value);Assert.Equal(12,c.Complete(p,11,g)!.Value.Value);Assert.Equal(13,c.Complete(p,12,g)!.Value.Value);}
    [Fact] public void Unresolved_earlier_offset_blocks_later_completion(){var c=new KafkaOffsetCoordinator();var p=new TopicPartition("t",0);long g=c.Assign(p);c.Register(p,10);c.Register(p,11);c.Register(p,12);Assert.Null(c.Complete(p,11,g));Assert.Null(c.Complete(p,12,g));Assert.Equal(13,c.Complete(p,10,g)!.Value.Value);}
    [Fact] public void Independent_partitions_progress_independently(){var c=new KafkaOffsetCoordinator();var a=new TopicPartition("t",0);var b=new TopicPartition("t",1);long ga=c.Assign(a),gb=c.Assign(b);c.Register(a,4);c.Register(b,7);Assert.Equal(8,c.Complete(b,7,gb)!.Value.Value);Assert.Equal(5,c.Complete(a,4,ga)!.Value.Value);}
    [Fact] public async Task Concurrent_out_of_order_completions_never_skip_the_contiguous_boundary(){var c=new KafkaOffsetCoordinator();var p=new TopicPartition("t",0);long g=c.Assign(p);for(long i=10;i<110;i++)c.Register(p,i);long[] offsets=Enumerable.Range(10,100).Select(static x=>(long)x).OrderByDescending(static x=>x).ToArray();await Task.WhenAll(offsets.Select(i=>Task.Run(()=>c.Complete(p,i,g))));Assert.Equal(110,c.SafePosition(p)!.Offset.Value);}
    [Fact] public void Stale_settlement_after_Rebalance_revocation_cannot_commit(){var c=new KafkaOffsetCoordinator();var p=new TopicPartition("t",0);long g=c.Assign(p);c.Register(p,3);c.Revoke(p);Assert.Throws<InvalidOperationException>(()=>c.Complete(p,3,g));}
}
