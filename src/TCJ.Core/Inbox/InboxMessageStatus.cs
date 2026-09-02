namespace TCJ.Core.Inbox;

/// <summary>Durable transactional Inbox processing states.</summary>
public enum InboxMessageStatus
{
    /// <summary>The message has been durably received and is waiting for processing.</summary>
    Received = 0,
    /// <summary>A processor currently owns a bounded processing lease.</summary>
    Processing = 1,
    /// <summary>The handler, business changes, and final Inbox state committed successfully.</summary>
    Processed = 2,
    /// <summary>A bounded retry is scheduled for a future instant.</summary>
    RetryScheduled = 3,
    /// <summary>Automatic processing stopped after a permanent or exhausted failure.</summary>
    DeadLettered = 4
}
