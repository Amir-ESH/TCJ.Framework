using TCJ.Core.Outbox;

namespace TCJ.EntityFrameworkCore.Outbox;

/// <summary>
/// Durable Entity Framework Core representation of a transactional-outbox message.
/// </summary>
public sealed class OutboxMessage : IOutboxMessage
{
    private OutboxMessage() { }

    internal OutboxMessage(
        Guid id,
        DateTimeOffset occurredAtUtc,
        string eventType,
        string payload,
        DateTimeOffset createdAtUtc)
    {
        Id = id;
        OccurredAtUtc = occurredAtUtc;
        EventType = eventType;
        Payload = payload;
        AttemptCount = 0;
        NextAttemptAtUtc = createdAtUtc;
        CreatedAtUtc = createdAtUtc;
        UpdatedAtUtc = createdAtUtc;
    }

    /// <inheritdoc />
    public Guid Id { get; private set; }

    /// <inheritdoc />
    public DateTimeOffset OccurredAtUtc { get; private set; }

    /// <inheritdoc />
    public string EventType { get; private set; } = string.Empty;

    /// <inheritdoc />
    public string Payload { get; private set; } = string.Empty;

    /// <inheritdoc />
    public int AttemptCount { get; private set; }

    /// <summary>Gets the earliest UTC instant at which the message is eligible for another attempt.</summary>
    public DateTimeOffset NextAttemptAtUtc { get; private set; }

    /// <summary>Gets the UTC instant at which the current claim was acquired.</summary>
    public DateTimeOffset? LockedAtUtc { get; private set; }

    /// <summary>Gets the UTC instant at which the current claim expires.</summary>
    public DateTimeOffset? LockExpiresAtUtc { get; private set; }

    /// <summary>Gets the unique identifier of the current processing claim.</summary>
    public Guid? LockId { get; private set; }

    /// <summary>Gets the UTC instant at which processing completed successfully.</summary>
    public DateTimeOffset? ProcessedAtUtc { get; private set; }

    /// <summary>Gets the UTC instant at which automatic retry stopped permanently.</summary>
    public DateTimeOffset? DeadLetteredAtUtc { get; private set; }

    /// <summary>Gets the bounded exception type recorded for the most recent failure.</summary>
    public string? LastErrorType { get; private set; }

    /// <summary>Gets the bounded safe failure summary recorded for the most recent failure; raw exception messages are not stored by default.</summary>
    public string? LastError { get; private set; }

    /// <summary>Gets the UTC instant at which the record was created.</summary>
    public DateTimeOffset CreatedAtUtc { get; private set; }

    /// <summary>Gets the UTC instant at which operational metadata last changed.</summary>
    public DateTimeOffset UpdatedAtUtc { get; private set; }

    /// <summary>Gets the number of explicit replay requests accepted for this message.</summary>
    public int ReplayCount { get; private set; }

    /// <summary>Gets the most recent UTC replay instant.</summary>
    public DateTimeOffset? LastReplayedAtUtc { get; private set; }
}
