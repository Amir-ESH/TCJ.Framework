using System.Linq.Expressions;
using TCJ.EntityFrameworkCore.Abstractions;

namespace TCJ.EntityFrameworkCore.Searching.Internal;

internal interface IEntitySearchExecutor
{
    Task<bool> ExistsAsync(
        IReadDbContext readDb,
        LambdaExpression predicate,
        CancellationToken cancellationToken);

    Task<object?> FindAsync(
        IReadDbContext readDb,
        LambdaExpression predicate,
        CancellationToken cancellationToken);
}
