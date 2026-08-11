using System.Data;
using Microsoft.EntityFrameworkCore;
using TCJ.Core.Diagnostics;
using TCJ.EntityFrameworkCore.Abstractions;
using TCJ.EntityFrameworkCore.Diagnostics;
// Test for EF Core wide-routing
namespace TCJ.EntityFrameworkCore.UnitOfWork;

/// <summary>
/// Entity Framework Core implementation of a persistence and transaction boundary.
/// The scoped dependency-injection container retains ownership of the DbContext.
/// </summary>
public sealed class EfUnitOfWork : IUnitOfWork
{
    private readonly IWriteDbContext _db;

    /// <summary>
    /// Initializes the unit of work for the supplied write context.
    /// </summary>
    public EfUnitOfWork(IWriteDbContext db)
    {
        ArgumentNullException.ThrowIfNull(db);
        _db = db;
    }

    /// <inheritdoc />
    public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        string provider = EntityFrameworkCoreTelemetryDiagnostics.GetProviderName(_db);
        PersistenceTelemetryState telemetry =
            EntityFrameworkCoreTelemetryDiagnostics.StartPersistenceOperation(
                TcjDiagnosticNames.Activities.UnitOfWorkCommit,
                "commit",
                provider,
                PersistenceMetricKind.Commit);

        try
        {
            Task<int> operation = _db.SaveChangesAsync(cancellationToken);
            return telemetry.IsActive
                ? CompleteSaveChangesAsync(operation, telemetry, cancellationToken)
                : operation;
        }
        catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested)
        {
            telemetry.CompleteCanceled(exception);
            throw;
        }
        catch (Exception exception)
        {
            telemetry.CompleteFailure(exception);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<IUnitOfWorkTransaction> BeginTransactionAsync(
        CancellationToken cancellationToken = default)
    {
        EnsureNoActiveTransaction();
        string provider = EntityFrameworkCoreTelemetryDiagnostics.GetProviderName(_db);
        PersistenceTelemetryState telemetry =
            EntityFrameworkCoreTelemetryDiagnostics.StartPersistenceOperation(
                TcjDiagnosticNames.Activities.TransactionBegin,
                "transaction_begin",
                provider,
                PersistenceMetricKind.None);

        try
        {
            var transaction = await _db.Database
                .BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);

            telemetry.CompleteSuccess(transactionOutcome: TcjDiagnosticNames.Outcomes.Success);
            return new EfUnitOfWorkTransaction(transaction, provider);
        }
        catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested)
        {
            telemetry.CompleteCanceled(exception);
            throw;
        }
        catch (Exception exception)
        {
            telemetry.CompleteFailure(exception);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<IUnitOfWorkTransaction> BeginTransactionAsync(
        IsolationLevel isolationLevel,
        CancellationToken cancellationToken = default)
    {
        EnsureNoActiveTransaction();
        string provider = EntityFrameworkCoreTelemetryDiagnostics.GetProviderName(_db);
        PersistenceTelemetryState telemetry =
            EntityFrameworkCoreTelemetryDiagnostics.StartPersistenceOperation(
                TcjDiagnosticNames.Activities.TransactionBegin,
                "transaction_begin",
                provider,
                PersistenceMetricKind.None);

        try
        {
            var transaction = await _db.Database
                .BeginTransactionAsync(isolationLevel, cancellationToken)
                .ConfigureAwait(false);

            telemetry.CompleteSuccess(transactionOutcome: TcjDiagnosticNames.Outcomes.Success);
            return new EfUnitOfWorkTransaction(transaction, provider);
        }
        catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested)
        {
            telemetry.CompleteCanceled(exception);
            throw;
        }
        catch (Exception exception)
        {
            telemetry.CompleteFailure(exception);
            throw;
        }
    }

    private static async Task<int> CompleteSaveChangesAsync(
        Task<int> operation,
        PersistenceTelemetryState telemetry,
        CancellationToken cancellationToken)
    {
        try
        {
            int affectedRows = await operation.ConfigureAwait(false);
            telemetry.CompleteSuccess(affectedRows);
            return affectedRows;
        }
        catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested)
        {
            telemetry.CompleteCanceled(exception);
            throw;
        }
        catch (Exception exception)
        {
            telemetry.CompleteFailure(exception);
            throw;
        }
    }

    private void EnsureNoActiveTransaction()
    {
        if (_db.Database.CurrentTransaction is not null)
        {
            throw new InvalidOperationException(
                "The current DbContext already has an active transaction. " +
                "Complete and dispose it before starting another transaction.");
        }
    }
}
