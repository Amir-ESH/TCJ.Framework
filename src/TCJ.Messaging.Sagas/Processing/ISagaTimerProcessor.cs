namespace TCJ.Messaging.Sagas.Processing;

/// <summary>Runs due Saga-owned durable deadlines independently of any broker transport.</summary>
public interface ISagaTimerProcessor
{
    /// <summary>Claims and processes one bounded batch of due Saga timers.</summary>
    /// <param name="cancellationToken">Cancellation token for this processor iteration.</param>
    /// <returns>Counts describing claimed, completed, retry-scheduled, and exhausted timers.</returns>
    Task<SagaTimerProcessingResult> ProcessBatchAsync(CancellationToken cancellationToken = default);
}

/// <summary>Bounded result of one timer processor iteration.</summary>
/// <param name="ClaimedCount">Number of timer leases claimed for this iteration.</param>
/// <param name="CompletedCount">Number of timeout transitions committed successfully.</param>
/// <param name="RetryScheduledCount">Number of failed timers returned to a bounded retry schedule.</param>
/// <param name="FailedCount">Number of timers whose retry budget was exhausted.</param>
public readonly record struct SagaTimerProcessingResult(int ClaimedCount, int CompletedCount, int RetryScheduledCount, int FailedCount)
{
    /// <summary>Empty processor result.</summary>
    public static SagaTimerProcessingResult Empty => new(0, 0, 0, 0);
}
