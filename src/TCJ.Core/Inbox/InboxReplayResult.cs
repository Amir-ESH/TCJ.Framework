namespace TCJ.Core.Inbox;

/// <summary>Result of one explicit Inbox replay request.</summary>
/// <param name="InboxId">Durable Inbox row identifier.</param>
/// <param name="Replayed">Whether the record was made eligible for processing.</param>
public sealed record InboxReplayResult(Guid InboxId, bool Replayed);
