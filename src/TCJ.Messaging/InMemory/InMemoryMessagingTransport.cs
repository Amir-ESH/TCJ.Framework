using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using TCJ.Messaging.Configuration;
using TCJ.Messaging.Envelopes;
using TCJ.Messaging.HealthChecks;
using TCJ.Messaging.Publishing;
using TCJ.Messaging.Receiving;

namespace TCJ.Messaging.InMemory;

/// <summary>Snapshot of one in-memory dead-lettered delivery.</summary>
/// <param name="Envelope">Message envelope.</param><param name="Delivery">Delivery metadata.</param><param name="Options">Sanitized dead-letter options.</param>
public sealed record InMemoryDeadLetter(TransportMessageEnvelope Envelope, DeliveryContext Delivery, DeadLetterOptions Options);

/// <summary>Bounded non-durable in-memory adapter for tests and local development.</summary>
public sealed class InMemoryMessagingTransport : IMessagingTransportPublisher, IMessagingTransportBatchPublisher, IMessageReceiver, IMessagingTransportHealthProbe
{
    private readonly ConcurrentDictionary<string, SourceQueue> _sources = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<PublishResult> _publishPlan = new();
    private readonly ConcurrentQueue<InMemoryDeadLetter> _deadLetters = new();
    private readonly SemaphoreSlim _globalCapacity;
    private readonly TimeProvider _timeProvider;
    private readonly int _maximumBufferedMessages;
    private long _deliverySequence;
    private volatile bool _available = true;

    /// <summary>Creates the bounded non-durable adapter.</summary><param name="options">Messaging bounds.</param><param name="timeProvider">Time provider.</param>
    public InMemoryMessagingTransport(TcjMessagingOptions options, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(options); _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider)); options.Validate();
        _maximumBufferedMessages = options.MaximumBufferedMessages; _globalCapacity = new SemaphoreSlim(_maximumBufferedMessages, _maximumBufferedMessages);
    }
    /// <summary>Gets or sets artificial publish delay for deterministic tests.</summary>
    public TimeSpan PublishDelay { get; set; }
    /// <summary>Gets or sets whether the adapter is available.</summary>
    public bool IsAvailable { get => _available; set => _available = value; }
    /// <summary>Gets dead-letter snapshots.</summary>
    public IReadOnlyCollection<InMemoryDeadLetter> DeadLetters => _deadLetters.ToArray();
    /// <summary>Queues a planned publish result for deterministic tests.</summary><param name="result">Planned result.</param>
    public void EnqueuePublishResult(PublishResult result) { ArgumentNullException.ThrowIfNull(result); _publishPlan.Enqueue(result); }
    /// <summary>Injects two identical deliveries to exercise duplicate handling.</summary>
    /// <param name="message">Envelope to redeliver without changing its logical identity.</param>
    /// <param name="source">Source from which the duplicate deliveries should be received.</param>
    /// <param name="cancellationToken">Token used to cancel the enqueue operation.</param>
    public async Task InjectDuplicateAsync(TransportMessageEnvelope message, string source, CancellationToken cancellationToken = default)
    { ArgumentNullException.ThrowIfNull(message); ArgumentException.ThrowIfNullOrWhiteSpace(source); await EnqueueAsync(new QueuedMessage(message, source, 1), cancellationToken); await EnqueueAsync(new QueuedMessage(message, source, 1), cancellationToken); }

    /// <inheritdoc />
    public async Task<PublishResult> PublishAsync(TransportMessageEnvelope message, PublishContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message); ArgumentNullException.ThrowIfNull(context); cancellationToken.ThrowIfCancellationRequested();
        if (!_available) return new PublishResult(PublishOutcome.TransientFailure, FailureCategory: MessagingFailureCategory.TransientConnection, FailureType: nameof(MessagingFailureCategory.TransientConnection));
        if (PublishDelay > TimeSpan.Zero) await Task.Delay(PublishDelay, _timeProvider, cancellationToken).ConfigureAwait(false);
        if (_publishPlan.TryDequeue(out PublishResult? planned) && !planned.IsSuccess) return planned;
        string source = string.IsNullOrWhiteSpace(context.Destination) ? "default" : context.Destination!;
        await EnqueueAsync(new QueuedMessage(message, source, 1), cancellationToken).ConfigureAwait(false);
        return planned is { IsSuccess: true } ? planned : PublishResult.Published(message.MessageId);
    }
    /// <inheritdoc />
    public async Task<IReadOnlyList<PublishResult>> PublishBatchAsync(IReadOnlyList<TransportMessageEnvelope> messages, PublishContext context, CancellationToken cancellationToken = default)
    { ArgumentNullException.ThrowIfNull(messages); var results = new PublishResult[messages.Count]; for (int i=0;i<messages.Count;i++) results[i]=await PublishAsync(messages[i],context,cancellationToken); return results; }
    /// <inheritdoc />
    public async IAsyncEnumerable<ReceivedMessage> ReceiveAsync(ReceiveContext context, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context); ArgumentException.ThrowIfNullOrWhiteSpace(context.Source); SourceQueue source = GetOrCreateSource(context.Source);
        await foreach (QueuedMessage queued in source.Channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            _globalCapacity.Release(); long sequence=Interlocked.Increment(ref _deliverySequence); string id="inmemory-"+sequence.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var delivery=new DeliveryContext(id,queued.Attempt,_timeProvider.GetUtcNow(),queued.Source,context.Subscription,sequenceNumber:sequence);
            yield return new ReceivedMessage(queued.Envelope,delivery,new InMemorySettlement(this,queued,delivery));
        }
    }
    /// <inheritdoc />
    public ValueTask<bool> IsReadyAsync(CancellationToken cancellationToken = default) { cancellationToken.ThrowIfCancellationRequested(); return ValueTask.FromResult(_available); }

    internal async Task RequeueAsync(QueuedMessage queued, TimeSpan? delay, CancellationToken token)
    { if(delay is { } d){ if(d<TimeSpan.Zero||d>TimeSpan.FromMinutes(5)) throw new ArgumentOutOfRangeException(nameof(delay)); if(d>TimeSpan.Zero) await Task.Delay(d,_timeProvider,token); } await EnqueueAsync(queued with { Attempt=checked(queued.Attempt+1)},token); }
    internal void DeadLetter(QueuedMessage queued,DeliveryContext delivery,DeadLetterOptions options)=>_deadLetters.Enqueue(new InMemoryDeadLetter(queued.Envelope,delivery,Sanitize(options)));
    private async ValueTask EnqueueAsync(QueuedMessage queued,CancellationToken token){ await _globalCapacity.WaitAsync(token); bool written=false; try{ SourceQueue source=GetOrCreateSource(queued.Source); await source.Channel.Writer.WriteAsync(queued,token); written=true;} finally{if(!written)_globalCapacity.Release();}}
    private SourceQueue GetOrCreateSource(string source)=>_sources.GetOrAdd(source,_=>new SourceQueue(_maximumBufferedMessages));
    private static DeadLetterOptions Sanitize(DeadLetterOptions o)=>new(){Reason=Bound(o.Reason,128),Description=Bound(o.Description,512),FailureType=Bound(o.FailureType,128),FailedAtUtc=o.FailedAtUtc?.ToUniversalTime(),Attempt=o.Attempt};
    private static string? Bound(string? v,int max)=>v is null?null:new string(v.Where(static c=>!char.IsControl(c)).Take(max).ToArray());
    internal sealed record QueuedMessage(TransportMessageEnvelope Envelope,string Source,int Attempt);
    private sealed class SourceQueue { public SourceQueue(int capacity){Channel=System.Threading.Channels.Channel.CreateBounded<QueuedMessage>(new BoundedChannelOptions(capacity){FullMode=BoundedChannelFullMode.Wait,SingleReader=false,SingleWriter=false,AllowSynchronousContinuations=false});} public Channel<QueuedMessage> Channel{get;} }
    private sealed class InMemorySettlement : IMessageSettlement
    {
        private readonly InMemoryMessagingTransport _owner; private readonly QueuedMessage _queued; private readonly DeliveryContext _delivery; private int _state;
        public InMemorySettlement(InMemoryMessagingTransport owner,QueuedMessage queued,DeliveryContext delivery){_owner=owner;_queued=queued;_delivery=delivery;}
        public Task CompleteAsync(CancellationToken token=default){token.ThrowIfCancellationRequested();SettleOnce();return Task.CompletedTask;}
        public async Task RetryAsync(RetrySettlementOptions options,CancellationToken token=default){ArgumentNullException.ThrowIfNull(options);Begin();try{await _owner.RequeueAsync(_queued,options.Delay,token);Complete();}catch{Reset();throw;}}
        public Task DeadLetterAsync(DeadLetterOptions options,CancellationToken token=default){token.ThrowIfCancellationRequested();ArgumentNullException.ThrowIfNull(options);SettleOnce();_owner.DeadLetter(_queued,_delivery,options);return Task.CompletedTask;}
        public async Task AbandonAsync(CancellationToken token=default){Begin();try{await _owner.RequeueAsync(_queued,null,token);Complete();}catch{Reset();throw;}}
        public Task DeferAsync(CancellationToken token=default){token.ThrowIfCancellationRequested();throw new MessagingCapabilityException("Defer");}
        private void SettleOnce(){if(Interlocked.CompareExchange(ref _state,1,0)!=0)throw new InvalidOperationException("A received message can be settled only once.");}
        private void Begin(){if(Interlocked.CompareExchange(ref _state,2,0)!=0)throw new InvalidOperationException("A received message can be settled only once.");}
        private void Complete()=>Volatile.Write(ref _state,1); private void Reset()=>Volatile.Write(ref _state,0);
    }
}
