using TCJ.Core.Inbox;

namespace TCJ.EntityFrameworkCore.Inbox;

/// <summary>Durable EF Core representation of one consumer-scoped inbound logical message.</summary>
internal sealed class InboxMessage
{
    private InboxMessage() { }

    internal InboxMessage(
        Guid id,
        string messageId,
        string consumerName,
        string messageType,
        int messageVersion,
        string payloadHash,
        string? payload,
        string? headersJson,
        DateTimeOffset receivedAtUtc,
        DateTimeOffset createdAtUtc,
        InboxMessageStatus status,
        int attemptCount,
        Guid? lockId,
        DateTimeOffset? lockedAtUtc,
        DateTimeOffset? lockExpiresAtUtc,
        string? correlationId,
        string? causationId)
    {
        Id = id;
        MessageId = messageId;
        ConsumerName = consumerName;
        MessageType = messageType;
        MessageVersion = messageVersion;
        PayloadHash = payloadHash;
        Payload = payload;
        HeadersJson = headersJson;
        ReceivedAtUtc = receivedAtUtc;
        Status = status;
        AttemptCount = attemptCount;
        LockId = lockId;
        LockedAtUtc = lockedAtUtc;
        LockExpiresAtUtc = lockExpiresAtUtc;
        CorrelationId = correlationId;
        CausationId = causationId;
        CreatedAtUtc = createdAtUtc;
        UpdatedAtUtc = createdAtUtc;
    }

    /// <summary>Gets the durable Inbox row identifier.</summary>
    public Guid Id { get; private set; }
    /// <summary>Gets the stable logical inbound message identifier.</summary>
    public string MessageId { get; private set; } = string.Empty;
    /// <summary>Gets the stable logical consumer boundary.</summary>
    public string ConsumerName { get; private set; } = string.Empty;
    /// <summary>Gets the stable registered logical message type.</summary>
    public string MessageType { get; private set; } = string.Empty;
    /// <summary>Gets the registered message schema version.</summary>
    public int MessageVersion { get; private set; }
    /// <summary>Gets the SHA-256 hash of the delivered payload.</summary>
    public string PayloadHash { get; private set; } = string.Empty;
    /// <summary>Gets the retained payload when payload retention is enabled.</summary>
    public string? Payload { get; private set; }
    /// <summary>Gets serialized allowlisted headers only; sensitive transport headers are never retained by default.</summary>
    public string? HeadersJson { get; private set; }
    /// <summary>Gets the original receive timestamp.</summary>
    public DateTimeOffset ReceivedAtUtc { get; private set; }
    /// <summary>Gets the UTC instant at which the current attempt started.</summary>
    public DateTimeOffset? StartedAtUtc { get; private set; }
    /// <summary>Gets the UTC instant at which processing committed successfully.</summary>
    public DateTimeOffset? ProcessedAtUtc { get; private set; }
    /// <summary>Gets the number of processing attempts recorded.</summary>
    public int AttemptCount { get; private set; }
    /// <summary>Gets the durable processing state.</summary>
    public InboxMessageStatus Status { get; private set; }
    /// <summary>Gets the current bounded processing lease identifier.</summary>
    public Guid? LockId { get; private set; }
    /// <summary>Gets the UTC instant at which the current lease was acquired.</summary>
    public DateTimeOffset? LockedAtUtc { get; private set; }
    /// <summary>Gets the UTC instant at which the current lease expires.</summary>
    public DateTimeOffset? LockExpiresAtUtc { get; private set; }
    /// <summary>Gets the next UTC retry eligibility instant.</summary>
    public DateTimeOffset? NextAttemptAtUtc { get; private set; }
    /// <summary>Gets the bounded failure category from the most recent failure.</summary>
    public string? LastErrorType { get; private set; }
    /// <summary>Gets a bounded safe failure summary that never contains the payload or exception message.</summary>
    public string? LastError { get; private set; }
    /// <summary>Gets the UTC instant at which the record became dead-lettered.</summary>
    public DateTimeOffset? DeadLetteredAtUtc { get; private set; }
    /// <summary>Gets the optional correlation identifier.</summary>
    public string? CorrelationId { get; private set; }
    /// <summary>Gets the optional causation identifier.</summary>
    public string? CausationId { get; private set; }
    /// <summary>Gets the UTC creation instant.</summary>
    public DateTimeOffset CreatedAtUtc { get; private set; }
    /// <summary>Gets the most recent UTC metadata update instant.</summary>
    public DateTimeOffset UpdatedAtUtc { get; private set; }
    /// <summary>Gets the number of accepted explicit replay requests.</summary>
    public int ReplayCount { get; private set; }
    /// <summary>Gets the most recent explicit replay request instant.</summary>
    public DateTimeOffset? LastReplayedAtUtc { get; private set; }
}
