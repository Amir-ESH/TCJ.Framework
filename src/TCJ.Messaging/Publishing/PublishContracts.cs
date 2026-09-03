using TCJ.Messaging.Envelopes;

namespace TCJ.Messaging.Publishing;

/// <summary>Stable publish outcomes independent from a broker SDK.</summary>
public enum PublishOutcome
{
    /// <summary>The adapter reports that the message was published.</summary>
    Published = 0,
    /// <summary>The adapter accepted the message for delivery.</summary>
    Accepted = 1,
    /// <summary>A retryable transport failure occurred.</summary>
    TransientFailure = 2,
    /// <summary>A non-retryable transport failure occurred.</summary>
    PermanentFailure = 3,
    /// <summary>The caller canceled publication.</summary>
    Canceled = 4,
    /// <summary>The bounded publish timeout expired.</summary>
    TimedOut = 5,
    /// <summary>The requested operation requires an unsupported adapter capability.</summary>
    UnsupportedCapability = 6
}

/// <summary>Bounded transport failure categories.</summary>
public enum MessagingFailureCategory
{
    /// <summary>A transient transport connection failure.</summary>
    TransientConnection = 0,
    /// <summary>A transient throttling or quota failure.</summary>
    TransientThrottle = 1,
    /// <summary>A transient timeout failure.</summary>
    TransientTimeout = 2,
    /// <summary>A permanent authentication failure.</summary>
    PermanentAuthentication = 3,
    /// <summary>A permanent authorization failure.</summary>
    PermanentAuthorization = 4,
    /// <summary>A permanent topology or destination failure.</summary>
    PermanentTopology = 5,
    /// <summary>A permanent serialization or contract failure.</summary>
    PermanentSerialization = 6,
    /// <summary>The payload exceeds a bound.</summary>
    PayloadTooLarge = 7,
    /// <summary>The caller canceled the operation.</summary>
    Canceled = 8,
    /// <summary>The adapter does not support the requested capability.</summary>
    UnsupportedCapability = 9,
    /// <summary>The failure could not be classified more specifically.</summary>
    Unknown = 10
}

/// <summary>Result of publishing one logical message.</summary>
/// <param name="Outcome">Stable publish outcome.</param>
/// <param name="TransportMessageId">Optional sanitized transport-assigned identifier.</param>
/// <param name="FailureCategory">Optional bounded failure category.</param>
/// <param name="FailureType">Optional bounded sanitized failure type.</param>
public sealed record PublishResult(PublishOutcome Outcome, string? TransportMessageId = null,
    MessagingFailureCategory? FailureCategory = null, string? FailureType = null)
{
    /// <summary>Gets whether the transport accepted or published the message.</summary>
    public bool IsSuccess => Outcome is PublishOutcome.Published or PublishOutcome.Accepted;
    /// <summary>Gets whether durable retry ownership may safely schedule another publish attempt.</summary>
    public bool IsRetryable => Outcome is PublishOutcome.TransientFailure or PublishOutcome.TimedOut ||
        FailureCategory is MessagingFailureCategory.TransientConnection or MessagingFailureCategory.TransientThrottle or MessagingFailureCategory.TransientTimeout;
    /// <summary>Creates a successful published result.</summary>
    /// <param name="transportMessageId">Optional sanitized transport identifier.</param><returns>A published result.</returns>
    public static PublishResult Published(string? transportMessageId = null) => new(PublishOutcome.Published, transportMessageId);
    /// <summary>Creates a successful accepted result.</summary>
    /// <param name="transportMessageId">Optional sanitized transport identifier.</param><returns>An accepted result.</returns>
    public static PublishResult Accepted(string? transportMessageId = null) => new(PublishOutcome.Accepted, transportMessageId);
    /// <summary>Creates an explicit unsupported-capability result.</summary>
    /// <param name="failureType">Bounded capability identifier.</param><returns>An unsupported result.</returns>
    public static PublishResult Unsupported(string failureType) =>
        new(PublishOutcome.UnsupportedCapability, null, MessagingFailureCategory.UnsupportedCapability, ValidateFailureType(failureType));

    private static string ValidateFailureType(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length > 128 || value.Any(char.IsControl))
            throw new ArgumentException("FailureType must be 128 characters or fewer and cannot contain control characters.", nameof(value));
        return value;
    }
}

/// <summary>Optional broker-neutral publish hints.</summary>
public sealed record PublishContext
{
    /// <summary>Gets an explicit destination, or null to use topology resolution.</summary>
    public string? Destination { get; init; }
    /// <summary>Gets an optional partitioning hint.</summary>
    public string? PartitionKey { get; init; }
    /// <summary>Gets an optional ordering hint.</summary>
    public string? OrderingKey { get; init; }
    /// <summary>Gets an optional time-to-live hint.</summary>
    public TimeSpan? TimeToLive { get; init; }
    /// <summary>Gets an optional scheduled publication time.</summary>
    public DateTimeOffset? ScheduledAtUtc { get; init; }
}

/// <summary>Transport-neutral publisher used by application and Outbox integration.</summary>
public interface IMessagePublisher
{
    /// <summary>Publishes one raw envelope.</summary>
    /// <param name="message">Raw envelope.</param><param name="context">Destination and hints.</param><param name="cancellationToken">Caller token.</param>
    /// <returns>Stable publication result.</returns>
    Task<PublishResult> PublishAsync(TransportMessageEnvelope message, PublishContext context, CancellationToken cancellationToken = default);
}

/// <summary>Optional bounded batch publisher.</summary>
public interface IMessageBatchPublisher
{
    /// <summary>Publishes a bounded batch with index-aligned results.</summary>
    /// <param name="messages">Messages.</param><param name="context">Shared context.</param><param name="cancellationToken">Caller token.</param>
    /// <returns>One result per input.</returns>
    Task<IReadOnlyList<PublishResult>> PublishBatchAsync(IReadOnlyList<TransportMessageEnvelope> messages, PublishContext context, CancellationToken cancellationToken = default);
}

/// <summary>Low-level adapter publisher.</summary>
public interface IMessagingTransportPublisher
{
    /// <summary>Publishes an already validated message.</summary>
    /// <param name="message">Validated message.</param><param name="context">Validated context.</param><param name="cancellationToken">Caller token.</param>
    /// <returns>Adapter result.</returns>
    Task<PublishResult> PublishAsync(TransportMessageEnvelope message, PublishContext context, CancellationToken cancellationToken = default);
}

/// <summary>Optional low-level adapter batch publisher.</summary>
public interface IMessagingTransportBatchPublisher
{
    /// <summary>Publishes a validated batch.</summary>
    /// <param name="messages">Validated messages.</param><param name="context">Validated context.</param><param name="cancellationToken">Caller token.</param>
    /// <returns>One result per input.</returns>
    Task<IReadOnlyList<PublishResult>> PublishBatchAsync(IReadOnlyList<TransportMessageEnvelope> messages, PublishContext context, CancellationToken cancellationToken = default);
}
