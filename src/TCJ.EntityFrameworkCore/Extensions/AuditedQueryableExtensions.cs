using TCJ.Core.Entities;

namespace TCJ.EntityFrameworkCore.Extensions;

/// <summary>
/// Provides reusable query filters for audited entities.
/// </summary>
public static class AuditedQueryableExtensions
{
    /// <summary>
    /// Filters audited entities by their creation instant. Both boundaries are inclusive.
    /// </summary>
    /// <typeparam name="TEntity">The entity type.</typeparam>
    /// <param name="query">The query to transform.</param>
    /// <param name="from">The from value.</param>
    /// <param name="to">The to value.</param>
    /// <returns>The result of the operation.</returns>
    public static IQueryable<TEntity> WhereCreatedOnInRange<TEntity>(this IQueryable<TEntity> query, DateTimeOffset? from, DateTimeOffset? to)
        where TEntity : class, IAuditedEntity
    {
        ArgumentNullException.ThrowIfNull(query);

        if (from.HasValue && to.HasValue && from.Value > to.Value)
        {
            throw new ArgumentException("The start instant cannot be greater than the end instant.", nameof(from));
        }

        if (from.HasValue)
        {
            query = query.Where(entity => entity.CreatedOn >= from.Value);
        }

        if (to.HasValue)
        {
            query = query.Where(entity => entity.CreatedOn <= to.Value);
        }

        return query;
    }
}
