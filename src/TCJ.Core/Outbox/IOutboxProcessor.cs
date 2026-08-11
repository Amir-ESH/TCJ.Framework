namespace TCJ.Core.Outbox;

/// <summary>
/// Processes a bounded batch of committed transactional-outbox messages.
/// </summary>
public interface IOutboxProcessor
{
    /// <summary>
    /// Claims and processes one bounded batch of eligible messages.
    /// </summary>
    /// <param name="cancellationToken">Caller cancellation token.</param>
    /// <returns>Safe aggregate processing metadata; event payloads are never returned.</returns>
    Task<OutboxProcessingResult> ProcessBatchAsync(CancellationToken cancellationToken = default);
}
