using System.Data;

namespace TCJ.EntityFrameworkCore.UnitOfWork;

/// <summary>
/// Coordinates persistence for a scoped Entity Framework Core context and
/// creates explicit transaction scopes when an operation requires one.
/// </summary>
public interface IUnitOfWork
{
    /// <summary>
    /// Persists all changes currently tracked by the write context.
    /// </summary>
    /// <param name="cancellationToken">A token used to cancel the operation.</param>
    /// <returns>The number of state entries written to the database.</returns>
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Begins a transaction using the database provider's default isolation level.
    /// The caller owns the returned transaction and must dispose it asynchronously.
    /// </summary>
    /// <param name="cancellationToken">A token used to cancel transaction creation.</param>
    Task<IUnitOfWorkTransaction> BeginTransactionAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Begins a transaction using the specified isolation level.
    /// The caller owns the returned transaction and must dispose it asynchronously.
    /// </summary>
    /// <param name="isolationLevel">The requested database isolation level.</param>
    /// <param name="cancellationToken">A token used to cancel transaction creation.</param>
    Task<IUnitOfWorkTransaction> BeginTransactionAsync(
        IsolationLevel isolationLevel,
        CancellationToken cancellationToken = default);
}
