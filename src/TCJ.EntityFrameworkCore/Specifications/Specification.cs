using System.Collections.ObjectModel;
using System.Linq.Expressions;

namespace TCJ.EntityFrameworkCore.Specifications;

/// <summary>
/// Base class for reusable Entity Framework Core query specifications.
/// </summary>
/// <typeparam name="TEntity">The entity type targeted by the specification.</typeparam>
public abstract class Specification<TEntity> : ISpecification<TEntity>
    where TEntity : class
{
    private readonly List<Expression<Func<TEntity, object?>>> _includes = [];
    private readonly ReadOnlyCollection<Expression<Func<TEntity, object?>>> _includesView;
    private readonly List<IOrderExpression<TEntity>> _orderExpressions = [];
    private readonly ReadOnlyCollection<IOrderExpression<TEntity>> _orderExpressionsView;

    /// <summary>
    /// Initializes an unfiltered specification.
    /// </summary>
    protected Specification()
    {
        _includesView = _includes.AsReadOnly();
        _orderExpressionsView = _orderExpressions.AsReadOnly();
    }

    /// <summary>
    /// Initializes a specification with the supplied filter criteria.
    /// </summary>
    /// <param name="criteria">The filter applied to the entity query.</param>
    protected Specification(Expression<Func<TEntity, bool>> criteria)
        : this()
    {
        ArgumentNullException.ThrowIfNull(criteria);
        Criteria = criteria;
    }

    /// <inheritdoc />
    public Expression<Func<TEntity, bool>>? Criteria { get; }

    /// <inheritdoc />
    public IReadOnlyList<Expression<Func<TEntity, object?>>> Includes => _includesView;

    /// <inheritdoc />
    public IReadOnlyList<IOrderExpression<TEntity>> OrderExpressions => _orderExpressionsView;

    /// <inheritdoc />
    public SpecificationTrackingBehavior TrackingBehavior { get; private set; } =
        SpecificationTrackingBehavior.NoTracking;

    /// <inheritdoc />
    public int? Skip { get; private set; }

    /// <inheritdoc />
    public int? Take { get; private set; }

    /// <inheritdoc />
    public bool IgnoreQueryFilters { get; private set; }

    /// <inheritdoc />
    public bool IsSplitQuery { get; private set; }

    /// <summary>
    /// Adds a navigation expression to the eager-loading graph.
    /// </summary>
    /// <param name="includeExpression">The include expression to add.</param>
    protected void AddInclude(Expression<Func<TEntity, object?>> includeExpression)
    {
        ArgumentNullException.ThrowIfNull(includeExpression);
        _includes.Add(includeExpression);
    }

    /// <summary>
    /// Applies the primary ascending ordering expression.
    /// </summary>
    /// <typeparam name="TKey">The key type.</typeparam>
    /// <param name="keySelector">The key selector used for ordering.</param>
    protected void ApplyOrderBy<TKey>(Expression<Func<TEntity, TKey>> keySelector)
    {
        EnsurePrimaryOrderHasNotBeenConfigured();
        AddOrderExpression(keySelector, SpecificationOrderDirection.Ascending);
    }

    /// <summary>
    /// Applies the primary descending ordering expression.
    /// </summary>
    /// <typeparam name="TKey">The key type.</typeparam>
    /// <param name="keySelector">The key selector used for ordering.</param>
    protected void ApplyOrderByDescending<TKey>(Expression<Func<TEntity, TKey>> keySelector)
    {
        EnsurePrimaryOrderHasNotBeenConfigured();
        AddOrderExpression(keySelector, SpecificationOrderDirection.Descending);
    }

    /// <summary>
    /// Applies a secondary ascending ordering expression.
    /// </summary>
    /// <typeparam name="TKey">The key type.</typeparam>
    /// <param name="keySelector">The key selector used for ordering.</param>
    protected void ApplyThenBy<TKey>(Expression<Func<TEntity, TKey>> keySelector)
    {
        EnsurePrimaryOrderHasBeenConfigured();
        AddOrderExpression(keySelector, SpecificationOrderDirection.Ascending);
    }

    /// <summary>
    /// Applies a secondary descending ordering expression.
    /// </summary>
    /// <typeparam name="TKey">The key type.</typeparam>
    /// <param name="keySelector">The key selector used for ordering.</param>
    protected void ApplyThenByDescending<TKey>(Expression<Func<TEntity, TKey>> keySelector)
    {
        EnsurePrimaryOrderHasBeenConfigured();
        AddOrderExpression(keySelector, SpecificationOrderDirection.Descending);
    }

    /// <summary>
    /// Applies zero-based offset pagination to the specification.
    /// </summary>
    /// <param name="skip">The number of records to skip.</param>
    /// <param name="take">The maximum number of records to take.</param>
    protected void ApplyPaging(int skip, int take)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(skip);
        ArgumentOutOfRangeException.ThrowIfLessThan(take, 1);

        Skip = skip;
        Take = take;
    }

    /// <summary>
    /// Enables change tracking for results returned by this specification.
    /// </summary>
    protected void AsTracking() =>
        TrackingBehavior = SpecificationTrackingBehavior.Tracking;

    /// <summary>
    /// Uses no-tracking queries while preserving identity resolution in the result set.
    /// </summary>
    protected void AsNoTrackingWithIdentityResolution() =>
        TrackingBehavior = SpecificationTrackingBehavior.NoTrackingWithIdentityResolution;

    /// <summary>
    /// Ignores global query filters configured for the entity type.
    /// </summary>
    protected void IgnoreGlobalQueryFilters() =>
        IgnoreQueryFilters = true;

    /// <summary>
    /// Executes collection includes as split queries.
    /// </summary>
    protected void UseSplitQuery() =>
        IsSplitQuery = true;

    private void AddOrderExpression<TKey>(
        Expression<Func<TEntity, TKey>> keySelector,
        SpecificationOrderDirection direction)
    {
        ArgumentNullException.ThrowIfNull(keySelector);
        _orderExpressions.Add(new OrderExpression<TEntity, TKey>(keySelector, direction));
    }

    private void EnsurePrimaryOrderHasNotBeenConfigured()
    {
        if (_orderExpressions.Count != 0)
        {
            throw new InvalidOperationException(
                "A primary ordering expression has already been configured. " +
                "Use ApplyThenBy or ApplyThenByDescending for additional ordering.");
        }
    }

    private void EnsurePrimaryOrderHasBeenConfigured()
    {
        if (_orderExpressions.Count == 0)
        {
            throw new InvalidOperationException(
                "A secondary ordering expression requires a primary ordering expression.");
        }
    }
}
