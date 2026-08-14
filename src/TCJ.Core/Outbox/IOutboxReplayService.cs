namespace TCJ.Core.Outbox;

/// <summary>
/// Re-enables an explicitly selected dead-lettered outbox message for controlled replay.
/// Authorization remains the responsibility of the host application.
/// </summary>
public interface IOutboxReplayService
{
    /// <summary>Replays the specified message while preserving its original message identifier.</summary>
    /// <param name="messageId">Stable identifier of the dead-lettered message to replay.</param>
    /// <param name="cancellationToken">Caller cancellation token.</param>
    /// <returns>Safe replay result indicating whether the message became eligible again.</returns>
    Task<OutboxReplayResult> ReplayAsync(Guid messageId, CancellationToken cancellationToken = default);
}
