using Microsoft.EntityFrameworkCore.Storage;
using TCJ.Core.Diagnostics;
using TCJ.EntityFrameworkCore.Diagnostics;

namespace TCJ.EntityFrameworkCore.UnitOfWork;

/// <summary>
/// Wraps an EF Core transaction without exposing provider-specific transaction APIs.
/// The transaction must be used sequentially with its owning DbContext.
/// </summary>
internal sealed class EfUnitOfWorkTransaction : IUnitOfWorkTransaction
{
    private readonly IDbContextTransaction _transaction;
    private readonly Guid _transactionId;
    private readonly string _provider;
    private TransactionState _state = TransactionState.Active;

    public EfUnitOfWorkTransaction(IDbContextTransaction transaction, string provider)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);

        _transaction = transaction;
        _transactionId = transaction.TransactionId;
        _provider = provider;
    }

    /// <inheritdoc />
    public Guid TransactionId => _transactionId;

    /// <inheritdoc />
    public async Task CommitAsync(CancellationToken cancellationToken = default)
    {
        EnsureActive();
        PersistenceTelemetryState telemetry =
            EntityFrameworkCoreTelemetryDiagnostics.StartPersistenceOperation(
                TcjDiagnosticNames.Activities.TransactionCommit,
                "transaction_commit",
                _provider,
                PersistenceMetricKind.None);

        try
        {
            await _transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            _state = TransactionState.Committed;
            telemetry.CompleteSuccess(transactionOutcome: TcjDiagnosticNames.Outcomes.Success);
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
    public async Task RollbackAsync(CancellationToken cancellationToken = default)
    {
        EnsureActive();
        PersistenceTelemetryState telemetry =
            EntityFrameworkCoreTelemetryDiagnostics.StartPersistenceOperation(
                TcjDiagnosticNames.Activities.TransactionRollback,
                "transaction_rollback",
                _provider,
                PersistenceMetricKind.Rollback);

        try
        {
            await _transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            _state = TransactionState.RolledBack;
            telemetry.CompleteSuccess(transactionOutcome: TcjDiagnosticNames.Outcomes.Success);
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
    public async ValueTask DisposeAsync()
    {
        if (_state == TransactionState.Disposed)
        {
            return;
        }

        await _transaction.DisposeAsync().ConfigureAwait(false);
        _state = TransactionState.Disposed;
    }

    private void EnsureActive()
    {
        if (_state == TransactionState.Disposed)
        {
            throw new ObjectDisposedException(nameof(EfUnitOfWorkTransaction));
        }

        if (_state != TransactionState.Active)
        {
            throw new InvalidOperationException(
                "The transaction has already been committed or rolled back.");
        }
    }

    private enum TransactionState
    {
        Active,
        Committed,
        RolledBack,
        Disposed
    }
}
