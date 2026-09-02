namespace TCJ.Core.Inbox;

/// <summary>Deletes only eligible terminal Inbox records according to bounded retention policy.</summary>
public interface IInboxCleanupService
{
    /// <summary>Deletes one bounded cleanup batch.</summary>
    /// <param name="cancellationToken">Caller cancellation token.</param>
    /// <returns>Safe aggregate cleanup result.</returns>
    Task<InboxCleanupResult> CleanupAsync(CancellationToken cancellationToken = default);
}
