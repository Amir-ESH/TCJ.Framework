using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Confluent.Kafka;
using TCJ.Messaging.Kafka.Configuration;
using TCJ.Messaging.Kafka.Diagnostics;
using TCJ.Messaging.Kafka.Publishing;
using TCJ.Messaging.Receiving;

namespace TCJ.Messaging.Kafka.Receiving;

internal sealed class KafkaMessageReceiver : IMessageReceiver
{
    private readonly TcjKafkaOptions _options; private readonly KafkaMessageMapper _mapper; private readonly KafkaTransportPublisher _publisher; private readonly TimeProvider _time;
    internal KafkaMessageReceiver(TcjKafkaOptions options,KafkaMessageMapper mapper,KafkaTransportPublisher publisher,TimeProvider time){_options=options;_mapper=mapper;_publisher=publisher;_time=time;}
    public async IAsyncEnumerable<ReceivedMessage> ReceiveAsync(ReceiveContext context,[EnumeratorCancellation] CancellationToken cancellationToken=default)
    {
        ArgumentNullException.ThrowIfNull(context); _options.Validate(); TcjKafkaOptions.ValidateTopic(context.Source,nameof(context.Source)); if(string.IsNullOrWhiteSpace(context.Subscription)) throw new ArgumentException("Kafka ReceiveContext.Subscription is required and is the authoritative consumer-group id.",nameof(context));
        await using var session=new KafkaReceiverSession(_options,_mapper,_publisher,_time,context.Source,context.Subscription!,cancellationToken); session.Start();
        await foreach(ReceivedMessage message in session.Messages.ReadAllAsync(cancellationToken).ConfigureAwait(false)) yield return message;
    }
}

internal sealed class KafkaReceiverSession : IKafkaSettlementOwner,IAsyncDisposable
{
    private sealed record Command(KafkaSettlementToken Token,TaskCompletionSource<bool> Completion);
    private readonly TcjKafkaOptions _options; private readonly KafkaMessageMapper _mapper; private readonly KafkaTransportPublisher _publisher; private readonly TimeProvider _time; private readonly string _topic; private readonly string _group; private readonly CancellationTokenSource _cts; private readonly Channel<ReceivedMessage> _messages; private readonly Channel<Command> _commands; private readonly SemaphoreSlim _slots; private readonly KafkaOffsetCoordinator _offsets=new(); private readonly Dictionary<TopicPartition,long> _generations=new(); private readonly HashSet<TopicPartition> _assigned=new(); private Task? _owner; private IConsumer<string,byte[]>? _consumer; private bool _paused;
    internal KafkaReceiverSession(TcjKafkaOptions options,KafkaMessageMapper mapper,KafkaTransportPublisher publisher,TimeProvider time,string topic,string group,CancellationToken outer){_options=options;_mapper=mapper;_publisher=publisher;_time=time;_topic=topic;_group=group;_cts=CancellationTokenSource.CreateLinkedTokenSource(outer);_messages=Channel.CreateBounded<ReceivedMessage>(new BoundedChannelOptions(options.MaximumBufferedMessages){SingleReader=true,SingleWriter=true,FullMode=BoundedChannelFullMode.Wait});_commands=Channel.CreateBounded<Command>(new BoundedChannelOptions(options.MaximumBufferedMessages*2){SingleReader=true,SingleWriter=false,FullMode=BoundedChannelFullMode.Wait});_slots=new SemaphoreSlim(options.MaximumBufferedMessages,options.MaximumBufferedMessages);}
    internal ChannelReader<ReceivedMessage> Messages=>_messages.Reader;
    internal void Start(){ if(_owner is not null) throw new InvalidOperationException("Kafka receiver session already started."); _owner=Task.Run(OwnerLoopAsync,CancellationToken.None); }
    public async Task CompleteAsync(KafkaSettlementToken token,CancellationToken cancellationToken){ var tcs=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously); await _commands.Writer.WriteAsync(new Command(token,tcs),cancellationToken).ConfigureAwait(false); await tcs.Task.WaitAsync(cancellationToken).ConfigureAwait(false); }
    private async Task OwnerLoopAsync()
    {
        try
        {
            var builder=new ConsumerBuilder<string,byte[]>(KafkaConfigFactory.Consumer(_options,_group));
            builder.SetPartitionsAssignedHandler((c,parts)=>{ KafkaDiagnostics.Rebalance(); KafkaDiagnostics.Assigned(parts.Count); foreach(TopicPartition p in parts){ if(_assigned.Count>=_options.MaximumTrackedPartitions&&!_assigned.Contains(p)) throw new InvalidOperationException("Kafka partition tracking bound exceeded."); _assigned.Add(p); _generations[p]=_offsets.Assign(p); } });
            builder.SetPartitionsRevokedHandler((c,parts)=>{ KafkaDiagnostics.Rebalance(); KafkaDiagnostics.Revoked(parts.Count); foreach(TopicPartitionOffset p in parts){ TopicPartition tp=p.TopicPartition; try{ TopicPartitionOffset? safe=_offsets.SafePosition(tp); if(safe is not null) c.Commit([safe]); } catch(KafkaException){ KafkaDiagnostics.CommitFailure(); } _offsets.Revoke(tp); _assigned.Remove(tp); _generations.Remove(tp); } });
            _consumer=builder.Build(); _consumer.Subscribe(_topic);
            while(!_cts.IsCancellationRequested)
            {
                DrainCommands();
                bool slot=_slots.Wait(0);
                if(!slot)
                {
                    if(!_paused&&_assigned.Count>0){_consumer.Pause(_assigned);KafkaDiagnostics.Paused(_assigned.Count);_paused=true;}
                    ConsumeResult<string,byte[]>? polled=_consumer.Consume(TimeSpan.FromMilliseconds(100));
                    if(polled is not null) _consumer.Seek(polled.TopicPartitionOffset);
                    continue;
                }
                if(_paused&&_assigned.Count>0){_consumer.Resume(_assigned);KafkaDiagnostics.Resumed(_assigned.Count);_paused=false;}
                ConsumeResult<string,byte[]>? record=null;
                try{record=_consumer.Consume(TimeSpan.FromMilliseconds(100));}
                catch(ConsumeException e) when(!e.Error.IsFatal){_slots.Release();continue;}
                if(record is null){_slots.Release();continue;}
                TopicPartition tp=record.TopicPartition; if(!_generations.TryGetValue(tp,out long generation)){_slots.Release();continue;}
                long actualGeneration=_offsets.Register(tp,record.Offset.Value); if(actualGeneration!=generation){_slots.Release();continue;}
                TCJ.Messaging.Envelopes.TransportMessageEnvelope envelope=_mapper.FromKafka(record); int attempt=KafkaMessageMapper.GetAttempt(record.Message.Headers); string deliveryId=$"{record.Topic}:{record.Partition.Value}:{record.Offset.Value}";
                var token=new KafkaSettlementToken(record.Topic,record.Partition.Value,record.Offset.Value,generation); var settlement=new KafkaMessageSettlement(this,_publisher,token,envelope,attempt,_options);
                var delivery=new DeliveryContext(deliveryId,attempt,_time.GetUtcNow(),record.Topic,_group,record.Partition.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),record.Offset.Value);
                KafkaDiagnostics.Consume(); await _messages.Writer.WriteAsync(new ReceivedMessage(envelope,delivery,settlement),_cts.Token).ConfigureAwait(false);
            }
        }
        catch(OperationCanceledException) when(_cts.IsCancellationRequested){}
        catch(Exception e){_messages.Writer.TryComplete(new InvalidOperationException("Kafka consumer session failed.",e)); return;}
        finally
        {
            try{DrainShutdown();}catch{}
            if(_consumer is not null){try{_consumer.Close();}catch{} _consumer.Dispose();}
            _messages.Writer.TryComplete(); _commands.Writer.TryComplete();
        }
    }
    private void DrainShutdown()
    {
        if(_consumer is null)return;
        if(_assigned.Count>0&&!_paused){_consumer.Pause(_assigned);KafkaDiagnostics.Paused(_assigned.Count);_paused=true;}
        long started=System.Diagnostics.Stopwatch.GetTimestamp();
        while(_slots.CurrentCount<_options.MaximumBufferedMessages&&System.Diagnostics.Stopwatch.GetElapsedTime(started)<_options.ShutdownTimeout)
        {
            DrainCommands();
            if(_slots.CurrentCount>=_options.MaximumBufferedMessages)break;
            try
            {
                ConsumeResult<string,byte[]>? polled=_consumer.Consume(TimeSpan.FromMilliseconds(50));
                if(polled is not null)_consumer.Seek(polled.TopicPartitionOffset);
            }
            catch(ConsumeException e) when(!e.Error.IsFatal){}
        }
        DrainCommands();
        foreach(TopicPartition tp in _assigned)
        {
            try{TopicPartitionOffset? safe=_offsets.SafePosition(tp);if(safe is not null){_consumer.Commit([safe]);KafkaDiagnostics.Commit();}}
            catch(KafkaException){KafkaDiagnostics.CommitFailure();}
        }
    }
    private void DrainCommands()
    {
        if(_consumer is null)return; while(_commands.Reader.TryRead(out Command? cmd))
        {
            try
            {
                var tp=new TopicPartition(cmd.Token.Topic,new Partition(cmd.Token.Partition)); Offset? next=_offsets.Complete(tp,cmd.Token.Offset,cmd.Token.Generation);
                if(next is not null)
                {
                    try{_consumer.Commit([new TopicPartitionOffset(tp,next.Value)]);KafkaDiagnostics.Commit();KafkaDiagnostics.CommitActivity();}
                    catch(KafkaException){KafkaDiagnostics.CommitFailure();}
                }
                cmd.Completion.TrySetResult(true);
            }
            catch(Exception e){cmd.Completion.TrySetException(e);}
            finally{_slots.Release();}
        }
    }
    public async ValueTask DisposeAsync(){_cts.Cancel(); if(_owner is not null){try{await _owner.WaitAsync(_options.ShutdownTimeout).ConfigureAwait(false);}catch(TimeoutException){}} _cts.Dispose();_slots.Dispose();}
}
