using Microsoft.EntityFrameworkCore;

namespace TCJ.EntityFrameworkCore.Specifications;

/// <summary>
/// Applies specification rules to Entity Framework Core queries.
/// </summary>
public static class SpecificationEvaluator
{
    /// <summary>
    /// Applies the complete specification, including eager loading, ordering and paging.
    /// </summary>
    /// <typeparam name="TEntity">The entity type.</typeparam>
    /// <param name="query">The query to transform.</param>
    /// <param name="specification">The specification to apply.</param>
    /// <returns>The configured query.</returns>
    public static IQueryable<TEntity> GetQuery<TEntity>(
        IQueryable<TEntity> query,
        ISpecification<TEntity> specification)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(specification);

        query = ApplyCriteriaAndQueryFilters(query, specification);

        foreach (var includeExpression in specification.Includes)
        {
            query = query.Include(includeExpression);
        }

        query = ApplyTracking(query, specification.TrackingBehavior);

        if (specification.IsSplitQuery)
        {
            query = query.AsSplitQuery();
        }

        query = ApplyOrdering(query, specification.OrderExpressions);
        ValidatePaging(specification);

        if (specification.Skip.HasValue)
        {
            query = query.Skip(specification.Skip.Value);
        }

        if (specification.Take.HasValue)
        {
            query = query.Take(specification.Take.Value);
        }

        return query;
    }

    /// <summary>
    /// Applies only filters that affect the logical result set. Includes, ordering,
    /// tracking and paging are intentionally excluded for count and existence queries.
    /// </summary>
    /// <typeparam name="TEntity">The entity type.</typeparam>
    /// <param name="query">The query to transform.</param>
    /// <param name="specification">The specification to apply.</param>
    /// <returns>The configured query.</returns>
    public static IQueryable<TEntity> GetCountQuery<TEntity>(
        IQueryable<TEntity> query,
        ISpecification<TEntity> specification)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(specification);

        return ApplyCriteriaAndQueryFilters(query, specification);
    }

    private static void ValidatePaging<TEntity>(ISpecification<TEntity> specification)
        where TEntity : class
    {
        bool hasSkip = specification.Skip.HasValue;
        bool hasTake = specification.Take.HasValue;

        if (hasSkip != hasTake)
        {
            throw new InvalidOperationException(
                "A paged specification must define both Skip and Take values.");
        }

        if (!hasSkip)
        {
            return;
        }

        if (specification.Skip < 0)
        {
            throw new InvalidOperationException(
                "A specification Skip value cannot be negative.");
        }

        if (specification.Take < 1)
        {
            throw new InvalidOperationException(
                "A specification Take value must be greater than zero.");
        }

        if (specification.OrderExpressions.Count == 0)
        {
            throw new InvalidOperationException(
                "A paged specification must define an ordering expression to produce deterministic results.");
        }
    }

    private static IQueryable<TEntity> ApplyCriteriaAndQueryFilters<TEntity>(
        IQueryable<TEntity> query,
        ISpecification<TEntity> specification)
        where TEntity : class
    {
        if (specification.IgnoreQueryFilters)
        {
            query = query.IgnoreQueryFilters();
        }

        if (specification.Criteria is not null)
        {
            query = query.Where(specification.Criteria);
        }

        return query;
    }

    private static IQueryable<TEntity> ApplyTracking<TEntity>(
        IQueryable<TEntity> query,
        SpecificationTrackingBehavior trackingBehavior)
        where TEntity : class =>
        trackingBehavior switch
        {
            SpecificationTrackingBehavior.NoTracking => query.AsNoTracking(),
            SpecificationTrackingBehavior.Tracking => query.AsTracking(),
            SpecificationTrackingBehavior.NoTrackingWithIdentityResolution =>
                query.AsNoTrackingWithIdentityResolution(),
            _ => throw new InvalidOperationException(
                $"Unsupported specification tracking behavior '{trackingBehavior}'.")
        };

    private static IQueryable<TEntity> ApplyOrdering<TEntity>(
        IQueryable<TEntity> query,
        IReadOnlyList<IOrderExpression<TEntity>> orderExpressions)
        where TEntity : class
    {
        if (orderExpressions.Count == 0)
        {
            return query;
        }

        IOrderedQueryable<TEntity> orderedQuery = orderExpressions[0].Apply(query);

        for (int index = 1; index < orderExpressions.Count; index++)
        {
            orderedQuery = orderExpressions[index].ApplyThen(orderedQuery);
        }

        return orderedQuery;
    }
}
