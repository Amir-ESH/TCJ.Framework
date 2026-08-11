namespace TCJ.EntityFrameworkCore.Outbox;

/// <summary>
/// Provider-specific durable storage operations required by the outbox processor.
/// Implementations must use short claims and must not hold database locks while handlers execute.
/// </summary>
public interface IOutboxStorage
{
    /// <summary>Gets the normalized provider name.</summary>
    string ProviderName { get; }

    /// <summary>Claims one bounded batch of eligible records by using a lease.</summary>
    /// <param name="now">Current UTC time used for eligibility and lease expiration.</param>
    /// <param name="cancellationToken">Caller cancellation token.</param>
    /// <returns>Claimed messages ordered deterministically for processing.</returns>
    Task<IReadOnlyList<OutboxMessage>> ClaimBatchAsync(DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Marks a currently claimed record processed.</summary>
    /// <param name="messageId">Stable message identifier.</param>
    /// <param name="lockId">Lease identifier owned by the current processor.</param>
    /// <param name="attempt">One-based delivery attempt number.</param>
    /// <param name="now">UTC completion time.</param>
    /// <param name="cancellationToken">Caller cancellation token.</param>
    Task MarkProcessedAsync(Guid messageId, Guid lockId, int attempt, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Records a transient failure and schedules the next bounded retry.</summary>
    /// <param name="messageId">Stable message identifier.</param>
    /// <param name="lockId">Lease identifier owned by the current processor.</param>
    /// <param name="attempt">One-based delivery attempt number.</param>
    /// <param name="nextAttemptAtUtc">UTC instant at which the message becomes eligible again.</param>
    /// <param name="errorType">Bounded normalized failure type.</param>
    /// <param name="error">Bounded safe diagnostic summary that excludes payloads and stack traces.</param>
    /// <param name="now">UTC failure time.</param>
    /// <param name="cancellationToken">Caller cancellation token.</param>
    Task ScheduleRetryAsync(Guid messageId, Guid lockId, int attempt, DateTimeOffset nextAttemptAtUtc, string errorType, string error, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Moves a terminally failed message to dead-letter state.</summary>
    /// <param name="messageId">Stable message identifier.</param>
    /// <param name="lockId">Lease identifier owned by the current processor.</param>
    /// <param name="attempt">One-based delivery attempt number.</param>
    /// <param name="errorType">Bounded normalized failure type.</param>
    /// <param name="error">Bounded safe diagnostic summary that excludes payloads and stack traces.</param>
    /// <param name="now">UTC failure time.</param>
    /// <param name="cancellationToken">Caller cancellation token.</param>
    Task DeadLetterAsync(Guid messageId, Guid lockId, int attempt, string errorType, string error, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Explicitly re-enables a dead-lettered message while preserving its identifier.</summary>
    /// <param name="messageId">Stable identifier of the dead-lettered message.</param>
    /// <param name="now">Current UTC time used to reset retry eligibility.</param>
    /// <param name="cancellationToken">Caller cancellation token.</param>
    /// <returns><see langword="true"/> when the message was replayed; otherwise <see langword="false"/>.</returns>
    Task<bool> ReplayAsync(Guid messageId, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Deletes one bounded batch of eligible processed messages.</summary>
    /// <param name="processedBeforeUtc">Only processed messages older than this UTC cutoff may be deleted.</param>
    /// <param name="batchSize">Maximum number of rows to delete.</param>
    /// <param name="cancellationToken">Caller cancellation token.</param>
    /// <returns>Number of processed rows deleted.</returns>
    Task<int> CleanupAsync(DateTimeOffset processedBeforeUtc, int batchSize, CancellationToken cancellationToken);

    /// <summary>Returns safe aggregate health metadata without payloads.</summary>
    /// <param name="now">Current UTC time used to calculate backlog age.</param>
    /// <param name="cancellationToken">Caller cancellation token.</param>
    /// <returns>Safe aggregate outbox health snapshot.</returns>
    Task<OutboxHealthSnapshot> GetHealthSnapshotAsync(DateTimeOffset now, CancellationToken cancellationToken);
}
