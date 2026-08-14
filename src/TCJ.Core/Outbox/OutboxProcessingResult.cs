namespace TCJ.Core.Outbox;

/// <summary>Safe aggregate result for one bounded outbox-processing batch.</summary>
/// <param name="ClaimedCount">Number of messages claimed for the batch.</param>
/// <param name="ProcessedCount">Number of messages marked processed.</param>
/// <param name="RetryScheduledCount">Number of messages scheduled for another attempt.</param>
/// <param name="DeadLetteredCount">Number of messages moved to a terminal dead-letter state.</param>
public sealed record OutboxProcessingResult(
    int ClaimedCount,
    int ProcessedCount,
    int RetryScheduledCount,
    int DeadLetteredCount)
{
    private static readonly OutboxProcessingResult EmptyResult = new(0, 0, 0, 0);

    /// <summary>Gets whether the batch claimed at least one message.</summary>
    public bool HasWork => ClaimedCount > 0;

    /// <summary>Returns an empty processing result.</summary>
    public static OutboxProcessingResult Empty => EmptyResult;
}
