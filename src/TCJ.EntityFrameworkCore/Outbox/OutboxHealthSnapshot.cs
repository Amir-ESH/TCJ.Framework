namespace TCJ.EntityFrameworkCore.Outbox;

/// <summary>Safe aggregate outbox state used by health checks and metrics.</summary>
/// <param name="PendingCount">Number of active pending or retryable messages.</param>
/// <param name="DeadLetterCount">Number of dead-lettered messages.</param>
/// <param name="OldestPendingAge">Age of the oldest pending message, or zero when the backlog is empty.</param>
public sealed record OutboxHealthSnapshot(long PendingCount, long DeadLetterCount, TimeSpan OldestPendingAge);
