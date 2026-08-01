using Microsoft.EntityFrameworkCore.Storage;

namespace TCJ.EntityFrameworkCore.UnitOfWork;

/// <summary>
/// Wraps an EF Core transaction without exposing provider-specific transaction APIs.
/// The transaction must be used sequentially with its owning DbContext.
/// </summary>
internal sealed class EfUnitOfWorkTransaction : IUnitOfWorkTransaction
{
    private readonly IDbContextTransaction _transaction;
    private readonly Guid _transactionId;
    private TransactionState _state = TransactionState.Active;

    public EfUnitOfWorkTransaction(IDbContextTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(transaction);

        _transaction = transaction;
        _transactionId = transaction.TransactionId;
    }

    /// <inheritdoc />
    public Guid TransactionId => _transactionId;

    /// <inheritdoc />
    public async Task CommitAsync(CancellationToken cancellationToken = default)
    {
        EnsureActive();

        await _transaction
            .CommitAsync(cancellationToken)
            .ConfigureAwait(false);

        _state = TransactionState.Committed;
    }

    /// <inheritdoc />
    public async Task RollbackAsync(CancellationToken cancellationToken = default)
    {
        EnsureActive();

        await _transaction
            .RollbackAsync(cancellationToken)
            .ConfigureAwait(false);

        _state = TransactionState.RolledBack;
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
