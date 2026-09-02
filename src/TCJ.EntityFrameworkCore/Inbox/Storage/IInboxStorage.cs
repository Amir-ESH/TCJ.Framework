using TCJ.Core.Inbox;

namespace TCJ.EntityFrameworkCore.Inbox.Storage;

internal interface IInboxStorage
{
    string ProviderName { get; }
    Task<InboxAcquireResult> AcquireInlineAsync(IncomingMessageEnvelope envelope, string payloadHash, string? storedPayload, string? headersJson, Guid lockId, DateTimeOffset now, CancellationToken cancellationToken);
    Task<InboxStoreResult> StoreDeferredAsync(IncomingMessageEnvelope envelope, string payloadHash, string storedPayload, string? headersJson, DateTimeOffset now, CancellationToken cancellationToken);
    Task<IReadOnlyList<InboxMessage>> ClaimBatchAsync(DateTimeOffset now, CancellationToken cancellationToken);
    Task<InboxMessage?> LockClaimedAsync(Guid inboxId, Guid lockId, CancellationToken cancellationToken);
    Task MarkProcessedAsync(Guid inboxId, Guid lockId, DateTimeOffset now, CancellationToken cancellationToken);
    Task RecordInlineFailureAsync(IncomingMessageEnvelope envelope, string payloadHash, string? storedPayload, string? headersJson, InboxFailureType failureType, string safeError, bool retry, DateTimeOffset? nextAttemptAtUtc, DateTimeOffset now, CancellationToken cancellationToken);
    Task ScheduleClaimedFailureAsync(Guid inboxId, Guid lockId, int attempt, InboxFailureType failureType, string safeError, bool retry, DateTimeOffset? nextAttemptAtUtc, DateTimeOffset now, CancellationToken cancellationToken);
    Task<bool> ReplayAsync(Guid inboxId, DateTimeOffset now, CancellationToken cancellationToken);
    Task<int> CleanupAsync(DateTimeOffset processedBeforeUtc, int batchSize, CancellationToken cancellationToken);
    Task<InboxHealthSnapshot> GetHealthSnapshotAsync(DateTimeOffset now, CancellationToken cancellationToken);
}
