namespace TCJ.Core.Outbox;

/// <summary>Result of an explicit outbox replay request.</summary>
/// <param name="MessageId">Stable message identifier.</param>
/// <param name="Replayed">Whether the message was made eligible for processing.</param>
public sealed record OutboxReplayResult(Guid MessageId, bool Replayed);
