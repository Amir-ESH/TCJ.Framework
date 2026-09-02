namespace TCJ.Core.Inbox;

/// <summary>Provides explicit host-authorized replay of a dead-lettered Inbox record.</summary>
public interface IInboxReplayService
{
    /// <summary>Re-enables one dead-lettered record while preserving its original logical message identity.</summary>
    /// <param name="inboxId">Durable Inbox row identifier.</param>
    /// <param name="cancellationToken">Caller cancellation token.</param>
    /// <returns>Replay decision and stable record identifier.</returns>
    Task<InboxReplayResult> ReplayAsync(Guid inboxId, CancellationToken cancellationToken = default);
}
