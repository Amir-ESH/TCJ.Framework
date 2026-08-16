using System.Linq.Expressions;
using TCJ.Core.Entities;
using TCJ.EntityFrameworkCore.Specifications;

namespace TCJ.EntityFrameworkCore.Repositories;

/// <summary>
/// Defines read operations for an entity with a strongly typed primary key.
/// </summary>
/// <typeparam name="TEntity">The entity type.</typeparam>
/// <typeparam name="TKey">The primary-key type.</typeparam>
public interface IReadRepository<TEntity, TKey>
    where TEntity : class, IEntity<TKey>
{
    /// <summary>
    /// Returns a no-tracking query for read-only composition.
    /// </summary>
    /// <returns>The configured query.</returns>
    IQueryable<TEntity> Query();

    /// <summary>
    /// Returns a tracking query for workflows that intentionally modify loaded entities.
    /// </summary>
    /// <returns>The configured query.</returns>
    IQueryable<TEntity> TrackedQuery();

    /// <summary>
    /// Returns a query shaped by the supplied specification.
    /// </summary>
    /// <param name="specification">The specification to apply.</param>
    /// <returns>The configured query.</returns>
    IQueryable<TEntity> Query(ISpecification<TEntity> specification);

    /// <summary>
    /// Retrieves an entity by primary key without enabling change tracking.
    /// </summary>
    /// <param name="id">The id value.</param>
    /// <param name="cancellationToken">A token that can be used to cancel the operation.</param>
    /// <returns>The resulting value.</returns>
    Task<TEntity?> GetByIdAsync(
        TKey id,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the first entity produced by the specification, or <see langword="null"/>.
    /// </summary>
    /// <param name="specification">The specification to apply.</param>
    /// <param name="cancellationToken">A token that can be used to cancel the operation.</param>
    /// <returns>The result of the operation.</returns>
    Task<TEntity?> FirstOrDefaultAsync(
        ISpecification<TEntity> specification,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns all entities without change tracking.
    /// </summary>
    /// <param name="cancellationToken">A token that can be used to cancel the operation.</param>
    /// <returns>The resulting value.</returns>
    Task<IReadOnlyList<TEntity>> ListAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns entities matching the supplied predicate without change tracking.
    /// </summary>
    /// <param name="predicate">The predicate used to evaluate values.</param>
    /// <param name="cancellationToken">A token that can be used to cancel the operation.</param>
    /// <returns>The resulting value.</returns>
    Task<IReadOnlyList<TEntity>> ListAsync(
        Expression<Func<TEntity, bool>> predicate,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns entities produced by the supplied specification.
    /// </summary>
    /// <param name="specification">The specification to apply.</param>
    /// <param name="cancellationToken">A token that can be used to cancel the operation.</param>
    /// <returns>The resulting value.</returns>
    Task<IReadOnlyList<TEntity>> ListAsync(
        ISpecification<TEntity> specification,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Determines whether the entity set contains any row.
    /// </summary>
    /// <param name="cancellationToken">A token that can be used to cancel the operation.</param>
    /// <returns>The result of the operation.</returns>
    Task<bool> AnyAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Determines whether any entity matches the supplied predicate.
    /// </summary>
    /// <param name="predicate">The predicate used to evaluate values.</param>
    /// <param name="cancellationToken">A token that can be used to cancel the operation.</param>
    /// <returns>The result of the operation.</returns>
    Task<bool> AnyAsync(
        Expression<Func<TEntity, bool>> predicate,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Determines whether any entity matches the specification criteria.
    /// Ordering, includes and paging do not affect this operation.
    /// </summary>
    /// <param name="specification">The specification to apply.</param>
    /// <param name="cancellationToken">A token that can be used to cancel the operation.</param>
    /// <returns>The result of the operation.</returns>
    Task<bool> AnyAsync(
        ISpecification<TEntity> specification,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Counts all entities in the set.
    /// </summary>
    /// <param name="cancellationToken">A token that can be used to cancel the operation.</param>
    /// <returns>The result of the operation.</returns>
    Task<int> CountAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Counts entities matching the supplied predicate.
    /// </summary>
    /// <param name="predicate">The predicate used to evaluate values.</param>
    /// <param name="cancellationToken">A token that can be used to cancel the operation.</param>
    /// <returns>The result of the operation.</returns>
    Task<int> CountAsync(
        Expression<Func<TEntity, bool>> predicate,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Counts entities matching the specification criteria.
    /// Ordering, includes and paging do not affect the count.
    /// </summary>
    /// <param name="specification">The specification to apply.</param>
    /// <param name="cancellationToken">A token that can be used to cancel the operation.</param>
    /// <returns>The result of the operation.</returns>
    Task<int> CountAsync(
        ISpecification<TEntity> specification,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Read repository for entities using <see cref="long"/> primary keys.
/// </summary>
/// <typeparam name="TEntity">The entity type.</typeparam>
public interface IReadRepository<TEntity> : IReadRepository<TEntity, long>
    where TEntity : class, IEntity<long>;
