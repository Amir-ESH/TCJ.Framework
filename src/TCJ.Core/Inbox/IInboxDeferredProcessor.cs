namespace TCJ.Core.Inbox;

/// <summary>Processes one bounded batch of durably received deferred Inbox messages.</summary>
public interface IInboxDeferredProcessor
{
    /// <summary>Claims and processes one bounded batch.</summary>
    /// <param name="cancellationToken">Caller cancellation token.</param>
    /// <returns>Safe aggregate processing counts.</returns>
    Task<InboxProcessingResult> ProcessBatchAsync(CancellationToken cancellationToken = default);
}
