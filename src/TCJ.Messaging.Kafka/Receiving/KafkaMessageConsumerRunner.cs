using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using TCJ.Messaging.Configuration;
using TCJ.Messaging.Integration;
using TCJ.Messaging.Kafka.Configuration;
using TCJ.Messaging.Kafka.Diagnostics;
using TCJ.Messaging.Receiving;

namespace TCJ.Messaging.Kafka.Receiving;

internal sealed class KafkaMessageConsumerRunner : IMessageConsumerRunner
{
    private sealed class PartitionQueue { internal readonly Queue<ReceivedMessage> Items=new(); internal bool Scheduled; }
    private readonly IMessageReceiver _receiver; private readonly IServiceScopeFactory _scopes; private readonly IMessagingStartupValidator _validator; private readonly TcjKafkaOptions _options; private readonly MessagingConsumerState _state; private readonly TimeProvider _time;
    internal KafkaMessageConsumerRunner(IMessageReceiver receiver,IServiceScopeFactory scopes,IMessagingStartupValidator validator,TcjKafkaOptions options,MessagingConsumerState state,TimeProvider time){_receiver=receiver;_scopes=scopes;_validator=validator;_options=options;_state=state;_time=time;}
    public async Task RunAsync(ReceiveContext context,CancellationToken cancellationToken=default)
    {
        ArgumentNullException.ThrowIfNull(context); if(string.IsNullOrWhiteSpace(context.Subscription)) throw new ArgumentException("Kafka consumer group is required in ReceiveContext.Subscription.",nameof(context)); await _validator.ValidateAsync(cancellationToken).ConfigureAwait(false); _state.Start();
        var queues=new Dictionary<string,PartitionQueue>(StringComparer.Ordinal); object sync=new(); var ready=Channel.CreateBounded<string>(new BoundedChannelOptions(_options.MaximumTrackedPartitions){SingleWriter=true,SingleReader=false,FullMode=BoundedChannelFullMode.Wait}); using var workersCts=new CancellationTokenSource(); Task[] workers=Enumerable.Range(0,_options.MaximumConcurrentPartitions).Select(_=>WorkerAsync()).ToArray();
        try
        {
            await foreach(ReceivedMessage message in _receiver.ReceiveAsync(context,cancellationToken).WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                string key=$"{message.Delivery.Source}:{message.Delivery.Partition ?? "-"}"; bool signal=false;
                lock(sync){if(!queues.TryGetValue(key,out PartitionQueue? q)){if(queues.Count>=_options.MaximumTrackedPartitions)throw new InvalidOperationException("Kafka partition scheduler tracking bound exceeded.");q=new();queues.Add(key,q);} if(q.Items.Count>=_options.MaximumBufferedMessages)throw new InvalidOperationException("Kafka partition queue bound exceeded.");q.Items.Enqueue(message);if(!q.Scheduled){q.Scheduled=true;signal=true;}}
                if(signal) await ready.Writer.WriteAsync(key,cancellationToken).ConfigureAwait(false);
            }
        }
        catch(OperationCanceledException) when(cancellationToken.IsCancellationRequested){}
        finally
        {
            ready.Writer.TryComplete(); try{await Task.WhenAll(workers).WaitAsync(_options.ShutdownTimeout).ConfigureAwait(false);}catch(TimeoutException){workersCts.Cancel();} _state.Stop();
        }
        async Task WorkerAsync()
        {
            await foreach(string key in ready.Reader.ReadAllAsync(workersCts.Token).ConfigureAwait(false))
            {
                ReceivedMessage? message=null; lock(sync){if(queues.TryGetValue(key,out PartitionQueue? q)&&q.Items.Count>0)message=q.Items.Dequeue();}
                if(message is null)continue; _state.MessageStarted(); long started=Stopwatch.GetTimestamp();
                try{await using AsyncServiceScope scope=_scopes.CreateAsyncScope(); InboxTransportBridge bridge=scope.ServiceProvider.GetRequiredService<InboxTransportBridge>(); await bridge.ProcessAsync(message,workersCts.Token).ConfigureAwait(false);}
                catch(OperationCanceledException) when(workersCts.IsCancellationRequested){}
                catch(KafkaStaleSettlementException e){_state.Fail(e.GetType().Name);}
                catch(Exception e){_state.Fail(e.GetType().Name);try{await message.Settlement.RetryAsync(new RetrySettlementOptions{Reason="UnhandledConsumerFailure"},CancellationToken.None).ConfigureAwait(false);}catch{}}
                finally{KafkaDiagnostics.Process(Stopwatch.GetElapsedTime(started).TotalMilliseconds);_state.MessageStopped();}
                bool again=false; lock(sync){PartitionQueue q=queues[key];if(q.Items.Count>0)again=true;else q.Scheduled=false;} if(again)await ready.Writer.WriteAsync(key,workersCts.Token).ConfigureAwait(false);
            }
        }
    }
}
