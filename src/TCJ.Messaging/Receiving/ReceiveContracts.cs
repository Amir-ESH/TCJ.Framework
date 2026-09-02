using System.Collections.ObjectModel;
using TCJ.Messaging.Envelopes;

namespace TCJ.Messaging.Receiving;

/// <summary>Transport-neutral settlement operations.</summary>
public enum MessageSettlement
{
    /// <summary>Completes a successfully committed delivery.</summary>
    Complete = 0,
    /// <summary>Requests retry or redelivery.</summary>
    Retry = 1,
    /// <summary>Moves a permanent failure to dead-letter handling.</summary>
    DeadLetter = 2,
    /// <summary>Releases without successful acknowledgement.</summary>
    Abandon = 3,
    /// <summary>Defers when explicitly supported.</summary>
    Defer = 4
}

/// <summary>Optional bounded retry settlement hints.</summary>
public sealed record RetrySettlementOptions
{
    /// <summary>Gets an optional bounded delay hint.</summary>
    public TimeSpan? Delay { get; init; }
    /// <summary>Gets an optional sanitized reason.</summary>
    public string? Reason { get; init; }
}

/// <summary>Sanitized dead-letter metadata.</summary>
public sealed record DeadLetterOptions
{
    /// <summary>Gets an optional bounded reason code.</summary>
    public string? Reason { get; init; }
    /// <summary>Gets an optional sanitized description.</summary>
    public string? Description { get; init; }
    /// <summary>Gets an optional bounded failure type.</summary>
    public string? FailureType { get; init; }
    /// <summary>Gets the UTC failure timestamp when known.</summary>
    public DateTimeOffset? FailedAtUtc { get; init; }
    /// <summary>Gets the delivery attempt when known.</summary>
    public int? Attempt { get; init; }
}

/// <summary>Broker-neutral settlement operations exposed for one delivery.</summary>
public interface IMessageSettlement
{
    /// <summary>Completes after durable application processing committed.</summary><param name="cancellationToken">Caller token.</param><returns>Settlement task.</returns>
    Task CompleteAsync(CancellationToken cancellationToken = default);
    /// <summary>Requests retry/redelivery.</summary><param name="options">Retry hints.</param><param name="cancellationToken">Caller token.</param><returns>Settlement task.</returns>
    Task RetryAsync(RetrySettlementOptions options, CancellationToken cancellationToken = default);
    /// <summary>Dead-letters one permanent failure.</summary><param name="options">Sanitized metadata.</param><param name="cancellationToken">Caller token.</param><returns>Settlement task.</returns>
    Task DeadLetterAsync(DeadLetterOptions options, CancellationToken cancellationToken = default);
    /// <summary>Abandons without successful completion.</summary><param name="cancellationToken">Caller token.</param><returns>Settlement task.</returns>
    Task AbandonAsync(CancellationToken cancellationToken = default);
    /// <summary>Defers when supported.</summary><param name="cancellationToken">Caller token.</param><returns>Settlement task.</returns>
    Task DeferAsync(CancellationToken cancellationToken = default);
}

/// <summary>Safe transport-neutral delivery metadata.</summary>
public sealed class DeliveryContext
{
    /// <summary>Creates validated delivery metadata.</summary>
    /// <param name="deliveryId">Stable transport delivery identifier.</param>
    /// <param name="deliveryAttempt">One-based delivery attempt.</param>
    /// <param name="receivedAtUtc">UTC time at which the delivery was received.</param>
    /// <param name="source">Stable source or destination name.</param>
    /// <param name="subscription">Optional subscription or consumer-group name.</param>
    /// <param name="partition">Optional transport partition identifier.</param>
    /// <param name="offset">Optional transport offset.</param>
    /// <param name="sequenceNumber">Optional transport sequence number.</param>
    /// <param name="lockExpiresAtUtc">Optional lock or lease expiration time.</param>
    /// <param name="extensions">Optional copied transport metadata that core processing does not depend on.</param>
    public DeliveryContext(string deliveryId, int deliveryAttempt, DateTimeOffset receivedAtUtc, string source,
        string? subscription = null, string? partition = null, long? offset = null, long? sequenceNumber = null,
        DateTimeOffset? lockExpiresAtUtc = null, IReadOnlyDictionary<string, string>? extensions = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deliveryId);
        if (deliveryId.Length > 256 || deliveryId.Any(char.IsControl)) throw new ArgumentException("DeliveryId must be 256 characters or fewer and cannot contain control characters.", nameof(deliveryId));
        if (deliveryAttempt <= 0) throw new ArgumentOutOfRangeException(nameof(deliveryAttempt));
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        if (source.Length > 128 || source.Any(char.IsControl)) throw new ArgumentException("Source must be 128 characters or fewer and cannot contain control characters.", nameof(source));
        DeliveryId = deliveryId; DeliveryAttempt = deliveryAttempt; ReceivedAtUtc = receivedAtUtc.ToUniversalTime(); Source = source;
        Subscription = subscription; Partition = partition; Offset = offset; SequenceNumber = sequenceNumber; LockExpiresAtUtc = lockExpiresAtUtc?.ToUniversalTime();
        Extensions = new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(extensions ?? new Dictionary<string, string>(), StringComparer.Ordinal));
    }
    /// <summary>Gets the stable concrete delivery identifier.</summary>
    public string DeliveryId { get; }
    /// <summary>Gets the one-based delivery attempt.</summary>
    public int DeliveryAttempt { get; }
    /// <summary>Gets receive time.</summary>
    public DateTimeOffset ReceivedAtUtc { get; }
    /// <summary>Gets source/destination.</summary>
    public string Source { get; }
    /// <summary>Gets optional subscription.</summary>
    public string? Subscription { get; }
    /// <summary>Gets optional partition.</summary>
    public string? Partition { get; }
    /// <summary>Gets optional offset.</summary>
    public long? Offset { get; }
    /// <summary>Gets optional sequence number.</summary>
    public long? SequenceNumber { get; }
    /// <summary>Gets optional lock expiry.</summary>
    public DateTimeOffset? LockExpiresAtUtc { get; }
    /// <summary>Gets copied adapter extensions.</summary>
    public IReadOnlyDictionary<string, string> Extensions { get; }
}

/// <summary>One received message plus settlement boundary.</summary>
public sealed class ReceivedMessage
{
    /// <summary>Creates a received delivery.</summary><param name="envelope">Envelope.</param><param name="delivery">Delivery metadata.</param><param name="settlement">Settlement.</param>
    public ReceivedMessage(TransportMessageEnvelope envelope, DeliveryContext delivery, IMessageSettlement settlement)
    { Envelope = envelope ?? throw new ArgumentNullException(nameof(envelope)); Delivery = delivery ?? throw new ArgumentNullException(nameof(delivery)); Settlement = settlement ?? throw new ArgumentNullException(nameof(settlement)); }
    /// <summary>Gets envelope.</summary>
    public TransportMessageEnvelope Envelope { get; }
    /// <summary>Gets delivery metadata.</summary>
    public DeliveryContext Delivery { get; }
    /// <summary>Gets settlement.</summary>
    public IMessageSettlement Settlement { get; }
}

/// <summary>Broker-neutral bounded receiver.</summary>
public interface IMessageReceiver
{
    /// <summary>Receives deliveries until cancellation.</summary><param name="context">Source selection.</param><param name="cancellationToken">Caller token.</param><returns>Bounded async stream.</returns>
    IAsyncEnumerable<ReceivedMessage> ReceiveAsync(ReceiveContext context, CancellationToken cancellationToken = default);
}

/// <summary>Explicit source and optional subscription.</summary>
public sealed record ReceiveContext
{
    /// <summary>Gets required source/destination.</summary>
    public required string Source { get; init; }
    /// <summary>Gets optional subscription/group.</summary>
    public string? Subscription { get; init; }
}

/// <summary>Runs bounded receive, Inbox processing, and settlement.</summary>
public interface IMessageConsumerRunner
{
    /// <summary>Runs until cancellation/terminal failure.</summary><param name="context">Receive context.</param><param name="cancellationToken">Stopping token.</param><returns>Loop task.</returns>
    Task RunAsync(ReceiveContext context, CancellationToken cancellationToken = default);
}
