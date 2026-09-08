using TCJ.Messaging.Kafka.Diagnostics;
using TCJ.Messaging.Kafka.Publishing;
using TCJ.Messaging.Publishing;
using TCJ.Messaging.Receiving;

namespace TCJ.Messaging.Kafka.Receiving;

internal interface IKafkaSettlementOwner
{
    Task CompleteAsync(KafkaSettlementToken token,CancellationToken cancellationToken);
}
internal readonly record struct KafkaSettlementToken(string Topic,int Partition,long Offset,long Generation);

internal sealed class KafkaMessageSettlement : IMessageSettlement
{
    private readonly IKafkaSettlementOwner _owner; private readonly KafkaTransportPublisher _publisher; private readonly KafkaSettlementToken _token; private readonly TCJ.Messaging.Envelopes.TransportMessageEnvelope _message; private readonly int _attempt; private readonly TCJ.Messaging.Kafka.Configuration.TcjKafkaOptions _options; private readonly SemaphoreSlim _once=new(1,1); private bool _settled;
    internal KafkaMessageSettlement(IKafkaSettlementOwner owner,KafkaTransportPublisher publisher,KafkaSettlementToken token,TCJ.Messaging.Envelopes.TransportMessageEnvelope message,int attempt,TCJ.Messaging.Kafka.Configuration.TcjKafkaOptions options){_owner=owner;_publisher=publisher;_token=token;_message=message;_attempt=attempt;_options=options;}
    public Task CompleteAsync(CancellationToken cancellationToken=default)=>ExecuteAsync(async t=>await _owner.CompleteAsync(_token,t).ConfigureAwait(false),cancellationToken);
    public Task RetryAsync(RetrySettlementOptions options,CancellationToken cancellationToken=default)
    { ArgumentNullException.ThrowIfNull(options); if(options.Delay is { } delay && delay>TimeSpan.Zero) throw new MessagingCapabilityException("DelayedRetry"); return ExecuteAsync(async t=>{ if(_attempt>=_options.MaximumProcessingAttempts){ await DeadCoreAsync(_attempt,new DeadLetterOptions{Reason=options.Reason,FailureType="MaximumProcessingAttemptsExceeded",Attempt=_attempt},t).ConfigureAwait(false); return;} var r=await _publisher.PublishRetryAsync(_message,_token.Topic,_attempt+1,options.Reason,t).ConfigureAwait(false); if(!r.IsSuccess) throw new InvalidOperationException("Kafka retry publication did not succeed; source offset remains unresolved."); KafkaDiagnostics.Retry(); await _owner.CompleteAsync(_token,t).ConfigureAwait(false);},cancellationToken); }
    public Task DeadLetterAsync(DeadLetterOptions options,CancellationToken cancellationToken=default){ArgumentNullException.ThrowIfNull(options); return ExecuteAsync(t=>DeadCoreAsync(options.Attempt ?? _attempt,options,t),cancellationToken);}
    public Task AbandonAsync(CancellationToken cancellationToken=default)=>throw new MessagingCapabilityException("Abandon");
    public Task DeferAsync(CancellationToken cancellationToken=default)=>throw new MessagingCapabilityException("Defer");
    private async Task DeadCoreAsync(int attempt,DeadLetterOptions? options,CancellationToken t){ var r=await _publisher.PublishDeadAsync(_message,_token.Topic,attempt,options?.Reason,options?.Description,options?.FailureType,t).ConfigureAwait(false); if(!r.IsSuccess) throw new InvalidOperationException("Kafka dead-letter publication did not succeed; source offset remains unresolved."); KafkaDiagnostics.DeadLetter(); await _owner.CompleteAsync(_token,t).ConfigureAwait(false); }
    private async Task ExecuteAsync(Func<CancellationToken,Task> action,CancellationToken token){ await _once.WaitAsync(token).ConfigureAwait(false); try{ if(_settled) throw new InvalidOperationException("Kafka delivery has already been settled."); await action(token).ConfigureAwait(false); _settled=true; } finally{_once.Release();} }
}
