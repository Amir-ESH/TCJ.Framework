namespace TCJ.Core.Inbox;

/// <summary>Safe aggregate result for one deferred Inbox processing batch.</summary>
/// <param name="ClaimedCount">Messages claimed by this worker.</param>
/// <param name="ProcessedCount">Messages committed successfully.</param>
/// <param name="RetryScheduledCount">Messages scheduled for a bounded retry.</param>
/// <param name="DeadLetteredCount">Messages moved to permanent failure state.</param>
public sealed record InboxProcessingResult(int ClaimedCount, int ProcessedCount, int RetryScheduledCount, int DeadLetteredCount)
{
    private static readonly InboxProcessingResult EmptyResult = new(0, 0, 0, 0);
    /// <summary>Gets whether this batch claimed any work.</summary>
    public bool HasWork => ClaimedCount > 0;
    /// <summary>Gets an empty batch result.</summary>
    public static InboxProcessingResult Empty => EmptyResult;
}
