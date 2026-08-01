using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using TCJ.EntityFrameworkCore.Abstractions;

namespace TCJ.EntityFrameworkCore.Searching.Internal;

internal sealed class EntitySearchExecutor<TEntity> : IEntitySearchExecutor
    where TEntity : class
{
    public Task<bool> ExistsAsync(
        IReadDbContext readDb,
        LambdaExpression predicate,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(readDb);

        return readDb
            .Set<TEntity>()
            .AsNoTracking()
            .AnyAsync(GetTypedPredicate(predicate), cancellationToken);
    }

    public async Task<object?> FindAsync(
        IReadDbContext readDb,
        LambdaExpression predicate,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(readDb);

        return await readDb
            .Set<TEntity>()
            .AsNoTracking()
            .FirstOrDefaultAsync(GetTypedPredicate(predicate), cancellationToken)
            .ConfigureAwait(false);
    }

    private static Expression<Func<TEntity, bool>> GetTypedPredicate(LambdaExpression predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);

        return predicate as Expression<Func<TEntity, bool>>
            ?? throw new ArgumentException(
                $"The supplied predicate is not valid for entity type '{typeof(TEntity).FullName}'.",
                nameof(predicate));
    }
}
