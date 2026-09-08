using System.Diagnostics;
using Confluent.Kafka;
using TCJ.Messaging.Envelopes;
using TCJ.Messaging.Kafka.Configuration;
using TCJ.Messaging.Kafka.Diagnostics;
using TCJ.Messaging.Publishing;

namespace TCJ.Messaging.Kafka.Publishing;

internal sealed class KafkaTransportPublisher : IMessagingTransportPublisher, IMessagingTransportBatchPublisher
{
    private readonly KafkaProducerManager _manager; private readonly KafkaMessageMapper _mapper; private readonly TcjKafkaOptions _options;
    internal KafkaTransportPublisher(KafkaProducerManager manager,KafkaMessageMapper mapper,TcjKafkaOptions options){_manager=manager;_mapper=mapper;_options=options;}
    public async Task<PublishResult> PublishAsync(TransportMessageEnvelope message,PublishContext context,CancellationToken cancellationToken=default)
    {
        ArgumentNullException.ThrowIfNull(message); ArgumentNullException.ThrowIfNull(context); string topic;
        try { topic=KafkaMessageMapper.ResolveTopic(_options,context); _=KafkaMessageMapper.ResolveKey(message,context); }
        catch(ArgumentException){ return new(PublishOutcome.PermanentFailure,FailureCategory:MessagingFailureCategory.PermanentTopology,FailureType:"InvalidTopicOrKey"); }
        return await PublishCoreAsync(topic,_mapper.ToKafka(message,context),message.MessageId,cancellationToken).ConfigureAwait(false);
    }
    public async Task<IReadOnlyList<PublishResult>> PublishBatchAsync(IReadOnlyList<TransportMessageEnvelope> messages,PublishContext context,CancellationToken cancellationToken=default)
    {
        ArgumentNullException.ThrowIfNull(messages); ArgumentNullException.ThrowIfNull(context); if(messages.Count>_options.MaximumBatchSize) throw new ArgumentOutOfRangeException(nameof(messages),$"Batch exceeds {_options.MaximumBatchSize} messages.");
        var results=new PublishResult[messages.Count]; for(int i=0;i<messages.Count;i++){ if(cancellationToken.IsCancellationRequested){ results[i]=new(PublishOutcome.Canceled,FailureCategory:MessagingFailureCategory.Canceled,FailureType:"Canceled"); continue;} results[i]=await PublishAsync(messages[i],context,cancellationToken).ConfigureAwait(false);} return results;
    }
    internal Task<PublishResult> PublishRetryAsync(TransportMessageEnvelope message,string source,int attempt,string? reason,CancellationToken token)
        => PublishSpecialAsync(message,source+_options.RetryTopicSuffix,attempt,false,reason,null,null,token);
    internal Task<PublishResult> PublishDeadAsync(TransportMessageEnvelope message,string source,int attempt,string? reason,string? description,string? failureType,CancellationToken token)
        => PublishSpecialAsync(message,source+_options.DeadLetterTopicSuffix,attempt,true,reason,description,failureType,token);
    private async Task<PublishResult> PublishSpecialAsync(TransportMessageEnvelope message,string topic,int attempt,bool dead,string? reason,string? description,string? failureType,CancellationToken token)
    {
        TcjKafkaOptions.ValidateTopic(topic,nameof(topic));
        var extra=new Dictionary<string,string>{{"tcj-attempt",attempt.ToString(System.Globalization.CultureInfo.InvariantCulture)},{dead?"tcj-dead-letter":"tcj-retry","true"}};
        AddSafe(extra,"tcj-failure-reason",reason,128); AddSafe(extra,"tcj-failure-type",failureType,128); AddSafe(extra,"tcj-failure-description",description,512);
        string activity=dead?"tcj.kafka.dead_letter.publish":"tcj.kafka.retry.publish";
        using Activity? a=KafkaDiagnostics.Start(activity,ActivityKind.Producer,topic);
        return await PublishCoreAsync(topic,_mapper.ToKafka(message,new PublishContext{Destination=topic},extra),message.MessageId,token).ConfigureAwait(false);
    }
    private static void AddSafe(IDictionary<string,string> headers,string name,string? value,int max)
    {
        if(string.IsNullOrWhiteSpace(value))return; string safe=new(value.Where(static c=>!char.IsControl(c)).Take(max).ToArray()); if(safe.Length>0)headers[name]=safe;
    }
    private async Task<PublishResult> PublishCoreAsync(string topic,Message<string,byte[]> payload,string id,CancellationToken token)
    {
        long started=Stopwatch.GetTimestamp(); using Activity? a=KafkaDiagnostics.Start("tcj.kafka.publish",ActivityKind.Producer,topic);
        try { using var cts=CancellationTokenSource.CreateLinkedTokenSource(token); cts.CancelAfter(_options.PublishTimeout); IProducer<string,byte[]> producer=await _manager.GetAsync(cts.Token).ConfigureAwait(false); DeliveryResult<string,byte[]> result=await producer.ProduceAsync(topic,payload,cts.Token).ConfigureAwait(false); if(result.Status!=PersistenceStatus.Persisted){ KafkaDiagnostics.PublishFail(Stopwatch.GetElapsedTime(started).TotalMilliseconds); return new(PublishOutcome.TransientFailure,FailureCategory:MessagingFailureCategory.TransientConnection,FailureType:"NotPersisted"); } KafkaDiagnostics.PublishOk(Stopwatch.GetElapsedTime(started).TotalMilliseconds); a?.SetStatus(ActivityStatusCode.Ok); return PublishResult.Published(id); }
        catch(OperationCanceledException) when(token.IsCancellationRequested){ KafkaDiagnostics.PublishFail(Stopwatch.GetElapsedTime(started).TotalMilliseconds); return new(PublishOutcome.Canceled,FailureCategory:MessagingFailureCategory.Canceled,FailureType:"Canceled"); }
        catch(OperationCanceledException){ KafkaDiagnostics.PublishFail(Stopwatch.GetElapsedTime(started).TotalMilliseconds); return new(PublishOutcome.TimedOut,FailureCategory:MessagingFailureCategory.TransientTimeout,FailureType:"PublishTimeout"); }
        catch(ProduceException<string,byte[]> e){ KafkaDiagnostics.PublishFail(Stopwatch.GetElapsedTime(started).TotalMilliseconds); return Classify(e.Error.Code.ToString(),e.Error.IsFatal); }
        catch(KafkaException e){ KafkaDiagnostics.PublishFail(Stopwatch.GetElapsedTime(started).TotalMilliseconds); return Classify(e.Error.Code.ToString(),e.Error.IsFatal); }
    }
    private static PublishResult Classify(string code,bool fatal){ string c=code.ToLowerInvariant(); if(c.Contains("auth")) return new(PublishOutcome.PermanentFailure,FailureCategory:c.Contains("authorization")?MessagingFailureCategory.PermanentAuthorization:MessagingFailureCategory.PermanentAuthentication,FailureType:"KafkaAuthentication"); if(c.Contains("topic")||c.Contains("partition")) return new(PublishOutcome.PermanentFailure,FailureCategory:MessagingFailureCategory.PermanentTopology,FailureType:"KafkaTopology"); return new(fatal?PublishOutcome.PermanentFailure:PublishOutcome.TransientFailure,FailureCategory:fatal?MessagingFailureCategory.Unknown:MessagingFailureCategory.TransientConnection,FailureType:fatal?"KafkaFatal":"KafkaTransient"); }
}
