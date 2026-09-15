using System.Data;
using System.Data.Common;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using TCJ.EntityFrameworkCore.Abstractions;
using TCJ.Messaging.Sagas.Configuration;
using TCJ.Messaging.Sagas.EntityFrameworkCore.Persistence;
using TCJ.Messaging.Sagas.EntityFrameworkCore.Processing;

namespace TCJ.Messaging.Sagas.EntityFrameworkCore.SqlServer.Processing;

internal sealed class SqlServerSagaProviderStorage<TDbContext>(TDbContext dbContext, TcjSagaOptions options) : ISagaProviderStorage
    where TDbContext : DbContext, IReadDbContext, IWriteDbContext
{
    public string ProviderName => "Microsoft.EntityFrameworkCore.SqlServer";

    public async Task ReserveCorrelationAsync(SagaCorrelation correlation, CancellationToken cancellationToken)
    {
        if (dbContext.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Saga correlation reservation for an Inbox message requires the existing Inbox database transaction.");
        try
        {
            await dbContext.Database.ExecuteSqlInterpolatedAsync($$"""
                INSERT INTO [TCJ_SagaCorrelations]
                ([Id],[SagaId],[SagaType],[CorrelationName],[ValueHash],[CreatedAtUtc],[RemovedAtUtc])
                VALUES ({{correlation.Id}},{{correlation.SagaId}},{{correlation.SagaType}},{{correlation.CorrelationName}},{{correlation.ValueHash}},{{correlation.CreatedAtUtc}},NULL);
                """, cancellationToken).ConfigureAwait(false);
        }
        catch (SqlException exception) when (exception.Number is 2601 or 2627)
        {
            throw new DbUpdateConcurrencyException("A concurrent Saga start reserved the same active correlation.", exception);
        }
    }

    public async Task<IReadOnlyList<SagaTimerClaim>> ClaimDueTimersAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        Guid lockId = Guid.CreateVersion7(now);
        DateTimeOffset expiresAt = now + options.TimerLeaseDuration;
        DbConnection connection = dbContext.Database.GetDbConnection();
        bool closeConnection = connection.State != ConnectionState.Open;
        if (closeConnection) await dbContext.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using DbCommand command = connection.CreateCommand();
            command.CommandText = """
                ;WITH [Candidates] AS
                (
                    SELECT TOP (@BatchSize) *
                    FROM [TCJ_SagaTimers] WITH (UPDLOCK, READPAST, ROWLOCK)
                    WHERE [Status] = N'Scheduled'
                      AND [DueAtUtc] <= @Now
                      AND ([LockId] IS NULL OR [LockExpiresAtUtc] <= @Now)
                    ORDER BY [DueAtUtc], [TimerId]
                )
                UPDATE [Candidates]
                SET [LockId] = @LockId,
                    [LockedAtUtc] = @Now,
                    [LockExpiresAtUtc] = @ExpiresAt,
                    [AttemptCount] = [AttemptCount] + 1,
                    [UpdatedAtUtc] = @Now
                OUTPUT inserted.[TimerId], inserted.[SagaId], inserted.[SagaType], inserted.[TimerName],
                       inserted.[TimeoutType], inserted.[DueAtUtc], inserted.[AttemptCount], inserted.[LockId];
                """;
            AddParameter(command, "@BatchSize", options.TimerBatchSize);
            AddParameter(command, "@Now", now);
            AddParameter(command, "@LockId", lockId);
            AddParameter(command, "@ExpiresAt", expiresAt);

            var claims = new List<SagaTimerClaim>(options.TimerBatchSize);
            await using DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                claims.Add(new SagaTimerClaim(
                    reader.GetGuid(0),
                    reader.GetGuid(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetString(4),
                    reader.GetFieldValue<DateTimeOffset>(5),
                    reader.GetInt32(6),
                    reader.GetGuid(7)));
            }
            return claims;
        }
        finally
        {
            if (closeConnection) await dbContext.Database.CloseConnectionAsync().ConfigureAwait(false);
        }
    }

    public async Task RecordTimerFailureAsync(
        Guid timerId,
        Guid lockId,
        int attempt,
        string failureType,
        bool retry,
        DateTimeOffset? nextAttemptAtUtc,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        SagaTimerStatus status = retry ? SagaTimerStatus.Scheduled : SagaTimerStatus.Failed;
        DateTimeOffset due = retry && nextAttemptAtUtc.HasValue ? nextAttemptAtUtc.Value : now;
        int affected = await dbContext.Set<SagaTimer>()
            .Where(timer => timer.TimerId == timerId && timer.LockId == lockId && timer.Status == SagaTimerStatus.Scheduled)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(timer => timer.Status, status)
                .SetProperty(timer => timer.AttemptCount, attempt)
                .SetProperty(timer => timer.DueAtUtc, due)
                .SetProperty(timer => timer.LockId, (Guid?)null)
                .SetProperty(timer => timer.LockedAtUtc, (DateTimeOffset?)null)
                .SetProperty(timer => timer.LockExpiresAtUtc, (DateTimeOffset?)null)
                .SetProperty(timer => timer.LastFailureType, BoundFailureType(failureType))
                .SetProperty(timer => timer.UpdatedAtUtc, now), cancellationToken)
            .ConfigureAwait(false);
        if (affected != 1) throw new DbUpdateConcurrencyException("Saga timer lease was lost while recording a timeout failure.");
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        DbParameter parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private static string BoundFailureType(string value) => value.Length <= 128 ? value : value[..128];
}
