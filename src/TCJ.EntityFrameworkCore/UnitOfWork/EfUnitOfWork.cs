using System.Data;
using Microsoft.EntityFrameworkCore;
using TCJ.EntityFrameworkCore.Abstractions;

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
    public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
        _db.SaveChangesAsync(cancellationToken);

    /// <inheritdoc />
    public async Task<IUnitOfWorkTransaction> BeginTransactionAsync(
        CancellationToken cancellationToken = default)
    {
        EnsureNoActiveTransaction();

        var transaction = await _db.Database
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        return new EfUnitOfWorkTransaction(transaction);
    }

    /// <inheritdoc />
    public async Task<IUnitOfWorkTransaction> BeginTransactionAsync(
        IsolationLevel isolationLevel,
        CancellationToken cancellationToken = default)
    {
        EnsureNoActiveTransaction();

        var transaction = await _db.Database
            .BeginTransactionAsync(isolationLevel, cancellationToken)
            .ConfigureAwait(false);

        return new EfUnitOfWorkTransaction(transaction);
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
