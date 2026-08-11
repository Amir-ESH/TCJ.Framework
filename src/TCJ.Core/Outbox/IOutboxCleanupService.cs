namespace TCJ.Core.Outbox;

/// <summary>
/// Removes eligible processed outbox records according to the configured retention policy.
/// </summary>
public interface IOutboxCleanupService
{
    /// <summary>Deletes one bounded cleanup batch.</summary>
    /// <param name="cancellationToken">Caller cancellation token.</param>
    /// <returns>Safe aggregate cleanup metadata.</returns>
    Task<OutboxCleanupResult> CleanupAsync(CancellationToken cancellationToken = default);
}
