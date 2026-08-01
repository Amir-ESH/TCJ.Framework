using System.Linq.Expressions;

namespace TCJ.EntityFrameworkCore.Specifications;

/// <summary>
/// Describes a reusable Entity Framework Core query shape for an entity type.
/// </summary>
/// <typeparam name="TEntity">The entity type targeted by the specification.</typeparam>
public interface ISpecification<TEntity>
    where TEntity : class
{
    /// <summary>
    /// Gets the optional filter applied to the entity query.
    /// </summary>
    Expression<Func<TEntity, bool>>? Criteria { get; }

    /// <summary>
    /// Gets the navigation expressions that should be eagerly loaded.
    /// </summary>
    IReadOnlyList<Expression<Func<TEntity, object?>>> Includes { get; }

    /// <summary>
    /// Gets the ordered sequence of primary and secondary sort expressions.
    /// </summary>
    IReadOnlyList<IOrderExpression<TEntity>> OrderExpressions { get; }

    /// <summary>
    /// Gets the tracking behavior applied to the query.
    /// </summary>
    SpecificationTrackingBehavior TrackingBehavior { get; }

    /// <summary>
    /// Gets the number of rows skipped when paging is enabled.
    /// </summary>
    int? Skip { get; }

    /// <summary>
    /// Gets the maximum number of rows returned when paging is enabled.
    /// </summary>
    int? Take { get; }

    /// <summary>
    /// Gets a value indicating whether global query filters should be ignored.
    /// </summary>
    bool IgnoreQueryFilters { get; }

    /// <summary>
    /// Gets a value indicating whether collection includes should be executed as split queries.
    /// </summary>
    bool IsSplitQuery { get; }
}
