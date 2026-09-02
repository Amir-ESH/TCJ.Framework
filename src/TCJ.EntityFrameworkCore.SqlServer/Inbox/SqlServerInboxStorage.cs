using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using TCJ.Core.Diagnostics;
using TCJ.Core.Inbox;
using TCJ.EntityFrameworkCore.Abstractions;
using TCJ.EntityFrameworkCore.Inbox;
using TCJ.EntityFrameworkCore.Inbox.Storage;

namespace TCJ.EntityFrameworkCore.SqlServer.Inbox;

internal sealed class SqlServerInboxStorage<TDbContext> : IInboxStorage
    where TDbContext : DbContext, IReadDbContext, IWriteDbContext
{
    private readonly TDbContext _dbContext;
    private readonly TcjInboxOptions _options;

    public SqlServerInboxStorage(TDbContext dbContext, TcjInboxOptions options)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _options.Validate();
    }

    public string ProviderName => TcjDiagnosticNames.Providers.SqlServer;

    public async Task<InboxAcquireResult> AcquireInlineAsync(IncomingMessageEnvelope envelope, string payloadHash, string? storedPayload, string? headersJson, Guid lockId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        EnsureTransaction();
        DateTimeOffset lockExpires = now + _options.LockDuration;
        Guid id = Guid.CreateVersion7(now);
        try
        {
            await _dbContext.Database.ExecuteSqlInterpolatedAsync($$"""
                INSERT INTO [TCJ_InboxMessages]
                ([Id],[MessageId],[ConsumerName],[MessageType],[MessageVersion],[PayloadHash],[Payload],[HeadersJson],[ReceivedAtUtc],[StartedAtUtc],[ProcessedAtUtc],[AttemptCount],[Status],[LockId],[LockedAtUtc],[LockExpiresAtUtc],[NextAttemptAtUtc],[LastErrorType],[LastError],[DeadLetteredAtUtc],[CorrelationId],[CausationId],[CreatedAtUtc],[UpdatedAtUtc],[ReplayCount],[LastReplayedAtUtc])
                VALUES
                ({{id}},{{envelope.MessageId}},{{envelope.Consumer}},{{envelope.MessageType}},{{envelope.MessageVersion}},{{payloadHash}},{{storedPayload}},{{headersJson}},{{envelope.ReceivedAtUtc}},{{now}},NULL,1,{{InboxMessageStatus.Processing.ToString()}},{{lockId}},{{now}},{{lockExpires}},NULL,NULL,NULL,NULL,{{envelope.CorrelationId}},{{envelope.CausationId}},{{now}},{{now}},0,NULL);
                """, cancellationToken).ConfigureAwait(false);
            InboxMessage created = await FindByIdentityAsync(envelope.Consumer, envelope.MessageId, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Inbox insert succeeded but the inserted record could not be read.");
            return new InboxAcquireResult(InboxAcquireKind.Acquired, created, 1, false);
        }
        catch (SqlException exception) when (IsUniqueViolation(exception))
        {
            // The database unique key is the source of truth for idempotency races.
        }

        InboxMessage existing = await FindByIdentityAsync(envelope.Consumer, envelope.MessageId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Inbox unique-key conflict was reported but the existing record could not be read.");
        if (!MatchesContract(existing, envelope, payloadHash))
        {
            await MarkPayloadConflictAsync(existing.Id, now, cancellationToken).ConfigureAwait(false);
            return new InboxAcquireResult(InboxAcquireKind.PayloadConflict, existing, existing.AttemptCount, true);
        }
        if (existing.Status == InboxMessageStatus.Processed || existing.ProcessedAtUtc.HasValue)
        {
            return new InboxAcquireResult(InboxAcquireKind.ProcessedDuplicate, existing, existing.AttemptCount, true);
        }
        if (existing.Status == InboxMessageStatus.DeadLettered || existing.DeadLetteredAtUtc.HasValue)
        {
            return new InboxAcquireResult(InboxAcquireKind.DeadLettered, existing, existing.AttemptCount, true);
        }
        if (existing.NextAttemptAtUtc.HasValue && existing.NextAttemptAtUtc.Value > now)
        {
            return new InboxAcquireResult(InboxAcquireKind.RetryNotDue, existing, existing.AttemptCount, true);
        }

        int acquired = await _dbContext.Set<InboxMessage>()
            .Where(message => message.ConsumerName == envelope.Consumer && message.MessageId == envelope.MessageId)
            .Where(message => message.PayloadHash == payloadHash)
            .Where(message => message.ProcessedAtUtc == null && message.DeadLetteredAtUtc == null)
            .Where(message => message.NextAttemptAtUtc == null || message.NextAttemptAtUtc <= now)
            .Where(message => message.LockId == null || message.LockExpiresAtUtc <= now)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(message => message.Status, InboxMessageStatus.Processing)
                .SetProperty(message => message.AttemptCount, message => message.AttemptCount + 1)
                .SetProperty(message => message.StartedAtUtc, now)
                .SetProperty(message => message.LockId, lockId)
                .SetProperty(message => message.LockedAtUtc, now)
                .SetProperty(message => message.LockExpiresAtUtc, lockExpires)
                .SetProperty(message => message.UpdatedAtUtc, now), cancellationToken)
            .ConfigureAwait(false);

        if (acquired == 1)
        {
            InboxMessage owned = await _dbContext.Set<InboxMessage>().AsNoTracking().SingleAsync(message => message.LockId == lockId, cancellationToken).ConfigureAwait(false);
            return new InboxAcquireResult(InboxAcquireKind.Acquired, owned, owned.AttemptCount, true);
        }

        existing = await FindByIdentityAsync(envelope.Consumer, envelope.MessageId, cancellationToken).ConfigureAwait(false) ?? existing;
        return new InboxAcquireResult(InboxAcquireKind.DuplicateInProgress, existing, existing.AttemptCount, true);
    }

    public async Task<InboxStoreResult> StoreDeferredAsync(IncomingMessageEnvelope envelope, string payloadHash, string storedPayload, string? headersJson, DateTimeOffset now, CancellationToken cancellationToken)
    {
        EnsureTransaction();
        Guid id = Guid.CreateVersion7(now);
        try
        {
            await _dbContext.Database.ExecuteSqlInterpolatedAsync($$"""
                INSERT INTO [TCJ_InboxMessages]
                ([Id],[MessageId],[ConsumerName],[MessageType],[MessageVersion],[PayloadHash],[Payload],[HeadersJson],[ReceivedAtUtc],[StartedAtUtc],[ProcessedAtUtc],[AttemptCount],[Status],[LockId],[LockedAtUtc],[LockExpiresAtUtc],[NextAttemptAtUtc],[LastErrorType],[LastError],[DeadLetteredAtUtc],[CorrelationId],[CausationId],[CreatedAtUtc],[UpdatedAtUtc],[ReplayCount],[LastReplayedAtUtc])
                VALUES
                ({{id}},{{envelope.MessageId}},{{envelope.Consumer}},{{envelope.MessageType}},{{envelope.MessageVersion}},{{payloadHash}},{{storedPayload}},{{headersJson}},{{envelope.ReceivedAtUtc}},NULL,NULL,0,{{InboxMessageStatus.Received.ToString()}},NULL,NULL,NULL,{{now}},NULL,NULL,NULL,{{envelope.CorrelationId}},{{envelope.CausationId}},{{now}},{{now}},0,NULL);
                """, cancellationToken).ConfigureAwait(false);
            InboxMessage created = await FindByIdentityAsync(envelope.Consumer, envelope.MessageId, cancellationToken).ConfigureAwait(false);
            return new InboxStoreResult(InboxAcquireKind.Acquired, created, false);
        }
        catch (SqlException exception) when (IsUniqueViolation(exception))
        {
            InboxMessage existing = await FindByIdentityAsync(envelope.Consumer, envelope.MessageId, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Inbox unique-key conflict was reported but the existing record could not be read.");
            if (!MatchesContract(existing, envelope, payloadHash))
            {
                await MarkPayloadConflictAsync(existing.Id, now, cancellationToken).ConfigureAwait(false);
                return new InboxStoreResult(InboxAcquireKind.PayloadConflict, existing, true);
            }
            if (existing.Status == InboxMessageStatus.DeadLettered) return new InboxStoreResult(InboxAcquireKind.DeadLettered, existing, true);
            if (existing.Status == InboxMessageStatus.Processed) return new InboxStoreResult(InboxAcquireKind.ProcessedDuplicate, existing, true);
            return new InboxStoreResult(InboxAcquireKind.DuplicateInProgress, existing, true);
        }
    }

    public async Task<IReadOnlyList<InboxMessage>> ClaimBatchAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        Guid lockId = Guid.CreateVersion7(now);
        DateTimeOffset lockExpires = now + _options.LockDuration;
        int batchSize = _options.BatchSize;
        string consumer = _options.ConsumerName;
        int claimed = await _dbContext.Database.ExecuteSqlInterpolatedAsync($$"""
            ;WITH [candidates] AS
            (
                SELECT TOP ({{batchSize}}) *
                FROM [TCJ_InboxMessages] WITH (UPDLOCK, READPAST, READCOMMITTEDLOCK)
                WHERE [ConsumerName] = {{consumer}}
                  AND [ProcessedAtUtc] IS NULL
                  AND [DeadLetteredAtUtc] IS NULL
                  AND ([NextAttemptAtUtc] IS NULL OR [NextAttemptAtUtc] <= {{now}})
                  AND ([LockExpiresAtUtc] IS NULL OR [LockExpiresAtUtc] <= {{now}})
                  AND [Status] IN ({{InboxMessageStatus.Received.ToString()}}, {{InboxMessageStatus.RetryScheduled.ToString()}}, {{InboxMessageStatus.Processing.ToString()}})
                ORDER BY [NextAttemptAtUtc], [ReceivedAtUtc], [Id]
            )
            UPDATE [candidates]
            SET [Status] = {{InboxMessageStatus.Processing.ToString()}},
                [AttemptCount] = [AttemptCount] + 1,
                [StartedAtUtc] = {{now}},
                [LockId] = {{lockId}},
                [LockedAtUtc] = {{now}},
                [LockExpiresAtUtc] = {{lockExpires}},
                [UpdatedAtUtc] = {{now}};
            """, cancellationToken).ConfigureAwait(false);
        if (claimed == 0) return [];
        return await _dbContext.Set<InboxMessage>().AsNoTracking().Where(message => message.LockId == lockId).OrderBy(message => message.ReceivedAtUtc).ThenBy(message => message.Id).Take(batchSize).ToArrayAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<InboxMessage?> LockClaimedAsync(Guid inboxId, Guid lockId, CancellationToken cancellationToken)
    {
        EnsureTransaction();
        return await _dbContext.Set<InboxMessage>()
            .FromSqlInterpolated($$"""
                SELECT * FROM [TCJ_InboxMessages] WITH (UPDLOCK, ROWLOCK)
                WHERE [Id] = {{inboxId}} AND [LockId] = {{lockId}} AND [ProcessedAtUtc] IS NULL AND [DeadLetteredAtUtc] IS NULL
                """)
            .AsNoTracking()
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task MarkProcessedAsync(Guid inboxId, Guid lockId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        int affected = await _dbContext.Set<InboxMessage>()
            .Where(message => message.Id == inboxId && message.LockId == lockId && message.ProcessedAtUtc == null && message.DeadLetteredAtUtc == null)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(message => message.Status, InboxMessageStatus.Processed)
                .SetProperty(message => message.ProcessedAtUtc, now)
                .SetProperty(message => message.NextAttemptAtUtc, (DateTimeOffset?)null)
                .SetProperty(message => message.LockId, (Guid?)null)
                .SetProperty(message => message.LockedAtUtc, (DateTimeOffset?)null)
                .SetProperty(message => message.LockExpiresAtUtc, (DateTimeOffset?)null)
                .SetProperty(message => message.LastErrorType, (string?)null)
                .SetProperty(message => message.LastError, (string?)null)
                .SetProperty(message => message.UpdatedAtUtc, now), cancellationToken)
            .ConfigureAwait(false);
        if (affected != 1) throw new InvalidOperationException($"Inbox lease for record '{inboxId}' was lost before commit.");
    }

    public async Task RecordInlineFailureAsync(IncomingMessageEnvelope envelope, string payloadHash, string? storedPayload, string? headersJson, InboxFailureType failureType, string safeError, bool retry, DateTimeOffset? nextAttemptAtUtc, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        Guid id = Guid.CreateVersion7(now);
        string status = (retry ? InboxMessageStatus.RetryScheduled : InboxMessageStatus.DeadLettered).ToString();
        DateTimeOffset? deadLetteredAt = retry ? null : now;
        try
        {
            await _dbContext.Database.ExecuteSqlInterpolatedAsync($$"""
                INSERT INTO [TCJ_InboxMessages]
                ([Id],[MessageId],[ConsumerName],[MessageType],[MessageVersion],[PayloadHash],[Payload],[HeadersJson],[ReceivedAtUtc],[StartedAtUtc],[ProcessedAtUtc],[AttemptCount],[Status],[LockId],[LockedAtUtc],[LockExpiresAtUtc],[NextAttemptAtUtc],[LastErrorType],[LastError],[DeadLetteredAtUtc],[CorrelationId],[CausationId],[CreatedAtUtc],[UpdatedAtUtc],[ReplayCount],[LastReplayedAtUtc])
                VALUES
                ({{id}},{{envelope.MessageId}},{{envelope.Consumer}},{{envelope.MessageType}},{{envelope.MessageVersion}},{{payloadHash}},{{storedPayload}},{{headersJson}},{{envelope.ReceivedAtUtc}},{{now}},NULL,1,{{status}},NULL,NULL,NULL,{{nextAttemptAtUtc}},{{failureType.ToString()}},{{safeError}},{{deadLetteredAt}},{{envelope.CorrelationId}},{{envelope.CausationId}},{{now}},{{now}},0,NULL);
                """, cancellationToken).ConfigureAwait(false);
        }
        catch (SqlException exception) when (IsUniqueViolation(exception))
        {
            await _dbContext.Set<InboxMessage>()
                .Where(message => message.ConsumerName == envelope.Consumer && message.MessageId == envelope.MessageId)
                .Where(message => message.PayloadHash == payloadHash)
                .Where(message => message.ProcessedAtUtc == null && message.DeadLetteredAtUtc == null)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(message => message.Status, retry ? InboxMessageStatus.RetryScheduled : InboxMessageStatus.DeadLettered)
                    .SetProperty(message => message.AttemptCount, message => message.AttemptCount + 1)
                    .SetProperty(message => message.NextAttemptAtUtc, nextAttemptAtUtc)
                    .SetProperty(message => message.DeadLetteredAtUtc, deadLetteredAt)
                    .SetProperty(message => message.LockId, (Guid?)null)
                    .SetProperty(message => message.LockedAtUtc, (DateTimeOffset?)null)
                    .SetProperty(message => message.LockExpiresAtUtc, (DateTimeOffset?)null)
                    .SetProperty(message => message.LastErrorType, failureType.ToString())
                    .SetProperty(message => message.LastError, safeError)
                    .SetProperty(message => message.UpdatedAtUtc, now), cancellationToken)
                .ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task ScheduleClaimedFailureAsync(Guid inboxId, Guid lockId, int attempt, InboxFailureType failureType, string safeError, bool retry, DateTimeOffset? nextAttemptAtUtc, DateTimeOffset now, CancellationToken cancellationToken)
    {
        InboxMessageStatus status = retry ? InboxMessageStatus.RetryScheduled : InboxMessageStatus.DeadLettered;
        DateTimeOffset? deadLetteredAt = retry ? null : now;
        int affected = await _dbContext.Set<InboxMessage>()
            .Where(message => message.Id == inboxId && message.LockId == lockId && message.ProcessedAtUtc == null && message.DeadLetteredAtUtc == null)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(message => message.Status, status)
                .SetProperty(message => message.AttemptCount, attempt)
                .SetProperty(message => message.NextAttemptAtUtc, nextAttemptAtUtc)
                .SetProperty(message => message.DeadLetteredAtUtc, deadLetteredAt)
                .SetProperty(message => message.LockId, (Guid?)null)
                .SetProperty(message => message.LockedAtUtc, (DateTimeOffset?)null)
                .SetProperty(message => message.LockExpiresAtUtc, (DateTimeOffset?)null)
                .SetProperty(message => message.LastErrorType, failureType.ToString())
                .SetProperty(message => message.LastError, safeError)
                .SetProperty(message => message.UpdatedAtUtc, now), cancellationToken)
            .ConfigureAwait(false);
        if (affected != 1) throw new InvalidOperationException($"Inbox lease for record '{inboxId}' was lost while recording failure.");
    }

    public async Task<bool> ReplayAsync(Guid inboxId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        int affected = await _dbContext.Set<InboxMessage>()
            .Where(message => message.Id == inboxId && message.Status == InboxMessageStatus.DeadLettered && message.ProcessedAtUtc == null)
            .Where(message => message.Payload != null)
            .Where(message => message.LockId == null || message.LockExpiresAtUtc <= now)
            .Where(message => message.LastErrorType != InboxFailureType.PayloadConflict.ToString())
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(message => message.Status, InboxMessageStatus.Received)
                .SetProperty(message => message.AttemptCount, 0)
                .SetProperty(message => message.NextAttemptAtUtc, now)
                .SetProperty(message => message.DeadLetteredAtUtc, (DateTimeOffset?)null)
                .SetProperty(message => message.LockId, (Guid?)null)
                .SetProperty(message => message.LockedAtUtc, (DateTimeOffset?)null)
                .SetProperty(message => message.LockExpiresAtUtc, (DateTimeOffset?)null)
                .SetProperty(message => message.LastErrorType, (string?)null)
                .SetProperty(message => message.LastError, (string?)null)
                .SetProperty(message => message.ReplayCount, message => message.ReplayCount + 1)
                .SetProperty(message => message.LastReplayedAtUtc, now)
                .SetProperty(message => message.UpdatedAtUtc, now), cancellationToken)
            .ConfigureAwait(false);
        return affected == 1;
    }

    public async Task<int> CleanupAsync(DateTimeOffset processedBeforeUtc, int batchSize, CancellationToken cancellationToken)
    {
        Guid[] ids = await _dbContext.Set<InboxMessage>().AsNoTracking()
            .Where(message => message.ConsumerName == _options.ConsumerName)
            .Where(message => message.Status == InboxMessageStatus.Processed && message.ProcessedAtUtc < processedBeforeUtc)
            .Where(message => message.LockId == null)
            .OrderBy(message => message.ProcessedAtUtc).ThenBy(message => message.Id)
            .Select(message => message.Id).Take(batchSize).ToArrayAsync(cancellationToken).ConfigureAwait(false);
        if (ids.Length == 0) return 0;
        return await _dbContext.Set<InboxMessage>()
            .Where(message => ids.Contains(message.Id) && message.Status == InboxMessageStatus.Processed && message.LockId == null)
            .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<InboxHealthSnapshot> GetHealthSnapshotAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        IQueryable<InboxMessage> active = _dbContext.Set<InboxMessage>().AsNoTracking()
            .Where(message => message.ConsumerName == _options.ConsumerName)
            .Where(message => message.ProcessedAtUtc == null && message.DeadLetteredAtUtc == null);
        long pending = await active.LongCountAsync(cancellationToken).ConfigureAwait(false);
        DateTimeOffset? oldest = await active.OrderBy(message => message.ReceivedAtUtc).Select(message => (DateTimeOffset?)message.ReceivedAtUtc).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        long dead = await _dbContext.Set<InboxMessage>().AsNoTracking().LongCountAsync(message => message.ConsumerName == _options.ConsumerName && message.Status == InboxMessageStatus.DeadLettered, cancellationToken).ConfigureAwait(false);
        return new InboxHealthSnapshot(pending, dead, oldest.HasValue && oldest.Value < now ? now - oldest.Value : TimeSpan.Zero);
    }

    private async Task<InboxMessage?> FindByIdentityAsync(string consumer, string messageId, CancellationToken cancellationToken) =>
        await _dbContext.Set<InboxMessage>().AsNoTracking().SingleOrDefaultAsync(message => message.ConsumerName == consumer && message.MessageId == messageId, cancellationToken).ConfigureAwait(false);

    private async Task MarkPayloadConflictAsync(Guid inboxId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        const string safeError = "Inbox duplicate identity was delivered with conflicting type, version, or payload metadata.";
        await _dbContext.Set<InboxMessage>()
            .Where(message => message.Id == inboxId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(message => message.LastErrorType, InboxFailureType.PayloadConflict.ToString())
                .SetProperty(message => message.LastError, safeError)
                .SetProperty(message => message.UpdatedAtUtc, now), cancellationToken)
            .ConfigureAwait(false);
    }

    private static bool MatchesContract(InboxMessage existing, IncomingMessageEnvelope envelope, string payloadHash) =>
        string.Equals(existing.PayloadHash, payloadHash, StringComparison.Ordinal)
        && string.Equals(existing.MessageType, envelope.MessageType, StringComparison.Ordinal)
        && existing.MessageVersion == envelope.MessageVersion;

    private void EnsureTransaction()
    {
        if (_dbContext.Database.CurrentTransaction is null) throw new InvalidOperationException("Transactional Inbox storage operation requires an active database transaction.");
    }

    private static bool IsUniqueViolation(SqlException exception) => exception.Number is 2601 or 2627;
}
