using Microsoft.EntityFrameworkCore;
using TCJ.Core.Diagnostics;
using TCJ.Core.Outbox;
using TCJ.EntityFrameworkCore.Abstractions;
using TCJ.EntityFrameworkCore.Outbox;
using TCJ.EntityFrameworkCore.Outbox.Processing;

namespace TCJ.EntityFrameworkCore.SqlServer.Outbox;

internal sealed class SqlServerOutboxStorage<TDbContext> : IOutboxStorage
    where TDbContext : DbContext, IReadDbContext, IWriteDbContext
{
    private readonly TDbContext _dbContext;
    private readonly TcjOutboxOptions _options;

    public SqlServerOutboxStorage(TDbContext dbContext, TcjOutboxOptions options)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _options.Validate();
    }

    public string ProviderName => TcjDiagnosticNames.Providers.SqlServer;

    public async Task<IReadOnlyList<OutboxMessage>> ClaimBatchAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Guid lockId = Guid.CreateVersion7(now);
        DateTimeOffset lockExpiresAt = now + _options.LockDuration;
        int batchSize = _options.BatchSize;

        int claimed = await _dbContext.Database.ExecuteSqlInterpolatedAsync($$"""
            ;WITH [candidates] AS
            (
                SELECT TOP ({{batchSize}}) *
                FROM [TCJ_OutboxMessages] WITH (UPDLOCK, READPAST, READCOMMITTEDLOCK)
                WHERE [ProcessedAtUtc] IS NULL
                  AND [DeadLetteredAtUtc] IS NULL
                  AND [NextAttemptAtUtc] <= {{now}}
                  AND ([LockExpiresAtUtc] IS NULL OR [LockExpiresAtUtc] <= {{now}})
                ORDER BY [NextAttemptAtUtc], [OccurredAtUtc], [Id]
            )
            UPDATE [candidates]
            SET [LockId] = {{lockId}},
                [LockedAtUtc] = {{now}},
                [LockExpiresAtUtc] = {{lockExpiresAt}},
                [UpdatedAtUtc] = {{now}};
            """, cancellationToken).ConfigureAwait(false);

        if (claimed == 0)
        {
            return [];
        }

        return await _dbContext.Set<OutboxMessage>()
            .AsNoTracking()
            .Where(message => message.LockId == lockId)
            .OrderBy(message => message.NextAttemptAtUtc)
            .ThenBy(message => message.OccurredAtUtc)
            .ThenBy(message => message.Id)
            .Take(batchSize)
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public Task MarkProcessedAsync(Guid messageId, Guid lockId, int attempt, DateTimeOffset now, CancellationToken cancellationToken) =>
        UpdateOwnedLeaseAsync(
            messageId,
            lockId,
            query => query.ExecuteUpdateAsync(setters => setters
                .SetProperty(message => message.ProcessedAtUtc, now)
                .SetProperty(message => message.AttemptCount, attempt)
                .SetProperty(message => message.NextAttemptAtUtc, now)
                .SetProperty(message => message.LockId, (Guid?)null)
                .SetProperty(message => message.LockedAtUtc, (DateTimeOffset?)null)
                .SetProperty(message => message.LockExpiresAtUtc, (DateTimeOffset?)null)
                .SetProperty(message => message.LastErrorType, (string?)null)
                .SetProperty(message => message.LastError, (string?)null)
                .SetProperty(message => message.UpdatedAtUtc, now), cancellationToken));

    public Task ScheduleRetryAsync(
        Guid messageId,
        Guid lockId,
        int attempt,
        DateTimeOffset nextAttemptAtUtc,
        string errorType,
        string error,
        DateTimeOffset now,
        CancellationToken cancellationToken) =>
        UpdateOwnedLeaseAsync(
            messageId,
            lockId,
            query => query.ExecuteUpdateAsync(setters => setters
                .SetProperty(message => message.AttemptCount, attempt)
                .SetProperty(message => message.NextAttemptAtUtc, nextAttemptAtUtc)
                .SetProperty(message => message.LockId, (Guid?)null)
                .SetProperty(message => message.LockedAtUtc, (DateTimeOffset?)null)
                .SetProperty(message => message.LockExpiresAtUtc, (DateTimeOffset?)null)
                .SetProperty(message => message.LastErrorType, errorType)
                .SetProperty(message => message.LastError, error)
                .SetProperty(message => message.UpdatedAtUtc, now), cancellationToken));

    public Task DeadLetterAsync(
        Guid messageId,
        Guid lockId,
        int attempt,
        string errorType,
        string error,
        DateTimeOffset now,
        CancellationToken cancellationToken) =>
        UpdateOwnedLeaseAsync(
            messageId,
            lockId,
            query => query.ExecuteUpdateAsync(setters => setters
                .SetProperty(message => message.AttemptCount, attempt)
                .SetProperty(message => message.DeadLetteredAtUtc, now)
                .SetProperty(message => message.LockId, (Guid?)null)
                .SetProperty(message => message.LockedAtUtc, (DateTimeOffset?)null)
                .SetProperty(message => message.LockExpiresAtUtc, (DateTimeOffset?)null)
                .SetProperty(message => message.LastErrorType, errorType)
                .SetProperty(message => message.LastError, error)
                .SetProperty(message => message.UpdatedAtUtc, now), cancellationToken));

    public async Task<bool> ReplayAsync(Guid messageId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        int affected = await _dbContext.Set<OutboxMessage>()
            .Where(message => message.Id == messageId)
            .Where(message => message.ProcessedAtUtc == null && message.DeadLetteredAtUtc != null)
            .Where(message => message.LockId == null || message.LockExpiresAtUtc <= now)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(message => message.DeadLetteredAtUtc, (DateTimeOffset?)null)
                .SetProperty(message => message.AttemptCount, 0)
                .SetProperty(message => message.NextAttemptAtUtc, now)
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
        Guid[] ids = await _dbContext.Set<OutboxMessage>()
            .AsNoTracking()
            .Where(message => message.ProcessedAtUtc != null && message.ProcessedAtUtc < processedBeforeUtc)
            .Where(message => message.LockId == null)
            .OrderBy(message => message.ProcessedAtUtc)
            .ThenBy(message => message.Id)
            .Select(message => message.Id)
            .Take(batchSize)
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);

        if (ids.Length == 0)
        {
            return 0;
        }

        return await _dbContext.Set<OutboxMessage>()
            .Where(message => ids.Contains(message.Id))
            .Where(message => message.ProcessedAtUtc != null && message.ProcessedAtUtc < processedBeforeUtc)
            .Where(message => message.LockId == null)
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<OutboxHealthSnapshot> GetHealthSnapshotAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        IQueryable<OutboxMessage> active = _dbContext.Set<OutboxMessage>()
            .AsNoTracking()
            .Where(message => message.ProcessedAtUtc == null && message.DeadLetteredAtUtc == null);

        long pendingCount = await active.LongCountAsync(cancellationToken).ConfigureAwait(false);
        DateTimeOffset? oldest = await active
            .OrderBy(message => message.OccurredAtUtc)
            .Select(message => (DateTimeOffset?)message.OccurredAtUtc)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        long deadLetters = await _dbContext.Set<OutboxMessage>()
            .AsNoTracking()
            .LongCountAsync(message => message.DeadLetteredAtUtc != null, cancellationToken)
            .ConfigureAwait(false);

        TimeSpan oldestAge = oldest.HasValue && oldest.Value < now ? now - oldest.Value : TimeSpan.Zero;
        return new OutboxHealthSnapshot(pendingCount, deadLetters, oldestAge);
    }

    private async Task UpdateOwnedLeaseAsync(
        Guid messageId,
        Guid lockId,
        Func<IQueryable<OutboxMessage>, Task<int>> update)
    {
        IQueryable<OutboxMessage> owned = _dbContext.Set<OutboxMessage>()
            .Where(message => message.Id == messageId)
            .Where(message => message.LockId == lockId)
            .Where(message => message.ProcessedAtUtc == null && message.DeadLetteredAtUtc == null);

        int affected = await update(owned).ConfigureAwait(false);
        if (affected != 1)
        {
            throw new OutboxLeaseLostException(messageId);
        }
    }
}
