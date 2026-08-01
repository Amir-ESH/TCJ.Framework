using System.Linq.Expressions;

namespace TCJ.EntityFrameworkCore.Specifications;

/// <summary>
/// Represents a strongly typed ordering expression in a specification.
/// </summary>
/// <typeparam name="TEntity">The entity type being ordered.</typeparam>
public interface IOrderExpression<TEntity>
    where TEntity : class
{
    /// <summary>
    /// Gets the ordering direction.
    /// </summary>
    SpecificationOrderDirection Direction { get; }

    /// <summary>
    /// Applies this expression as the primary ordering for a query.
    /// </summary>
    IOrderedQueryable<TEntity> Apply(IQueryable<TEntity> query);

    /// <summary>
    /// Applies this expression as a secondary ordering for an already ordered query.
    /// </summary>
    IOrderedQueryable<TEntity> ApplyThen(IOrderedQueryable<TEntity> query);
}

internal sealed class OrderExpression<TEntity, TKey> : IOrderExpression<TEntity>
    where TEntity : class
{
    private readonly Expression<Func<TEntity, TKey>> _keySelector;

    public OrderExpression(
        Expression<Func<TEntity, TKey>> keySelector,
        SpecificationOrderDirection direction)
    {
        ArgumentNullException.ThrowIfNull(keySelector);

        _keySelector = keySelector;
        Direction = direction;
    }

    public SpecificationOrderDirection Direction { get; }

    public IOrderedQueryable<TEntity> Apply(IQueryable<TEntity> query)
    {
        ArgumentNullException.ThrowIfNull(query);

        return Direction switch
        {
            SpecificationOrderDirection.Ascending => query.OrderBy(_keySelector),
            SpecificationOrderDirection.Descending => query.OrderByDescending(_keySelector),
            _ => throw new InvalidOperationException(
                $"Unsupported specification order direction '{Direction}'.")
        };
    }

    public IOrderedQueryable<TEntity> ApplyThen(IOrderedQueryable<TEntity> query)
    {
        ArgumentNullException.ThrowIfNull(query);

        return Direction switch
        {
            SpecificationOrderDirection.Ascending => query.ThenBy(_keySelector),
            SpecificationOrderDirection.Descending => query.ThenByDescending(_keySelector),
            _ => throw new InvalidOperationException(
                $"Unsupported specification order direction '{Direction}'.")
        };
    }
}
