namespace TCJ.EntityFrameworkCore.UnitOfWork;

/// <summary>
/// Represents an explicit database transaction created by an
/// <see cref="IUnitOfWork"/>.
/// </summary>
public interface IUnitOfWorkTransaction : IAsyncDisposable
{
    /// <summary>
    /// Gets the provider transaction identifier.
    /// </summary>
    Guid TransactionId { get; }

    /// <summary>
    /// Commits the transaction. Persist tracked changes through
    /// <see cref="IUnitOfWork.SaveChangesAsync"/> before calling this method.
    /// </summary>
    /// <param name="cancellationToken">A token used to cancel the commit.</param>
    Task CommitAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Rolls back the transaction.
    /// </summary>
    /// <param name="cancellationToken">A token used to cancel the rollback.</param>
    Task RollbackAsync(CancellationToken cancellationToken = default);
}
