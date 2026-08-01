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
    IQueryable<TEntity> Query();

    /// <summary>
    /// Returns a tracking query for workflows that intentionally modify loaded entities.
    /// </summary>
    IQueryable<TEntity> TrackedQuery();

    /// <summary>
    /// Returns a query shaped by the supplied specification.
    /// </summary>
    IQueryable<TEntity> Query(ISpecification<TEntity> specification);

    /// <summary>
    /// Retrieves an entity by primary key without enabling change tracking.
    /// </summary>
    Task<TEntity?> GetByIdAsync(
        TKey id,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the first entity produced by the specification, or <see langword="null"/>.
    /// </summary>
    Task<TEntity?> FirstOrDefaultAsync(
        ISpecification<TEntity> specification,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns all entities without change tracking.
    /// </summary>
    Task<IReadOnlyList<TEntity>> ListAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns entities matching the supplied predicate without change tracking.
    /// </summary>
    Task<IReadOnlyList<TEntity>> ListAsync(
        Expression<Func<TEntity, bool>> predicate,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns entities produced by the supplied specification.
    /// </summary>
    Task<IReadOnlyList<TEntity>> ListAsync(
        ISpecification<TEntity> specification,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Determines whether the entity set contains any row.
    /// </summary>
    Task<bool> AnyAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Determines whether any entity matches the supplied predicate.
    /// </summary>
    Task<bool> AnyAsync(
        Expression<Func<TEntity, bool>> predicate,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Determines whether any entity matches the specification criteria.
    /// Ordering, includes and paging do not affect this operation.
    /// </summary>
    Task<bool> AnyAsync(
        ISpecification<TEntity> specification,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Counts all entities in the set.
    /// </summary>
    Task<int> CountAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Counts entities matching the supplied predicate.
    /// </summary>
    Task<int> CountAsync(
        Expression<Func<TEntity, bool>> predicate,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Counts entities matching the specification criteria.
    /// Ordering, includes and paging do not affect the count.
    /// </summary>
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
