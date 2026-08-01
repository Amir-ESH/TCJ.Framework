using TCJ.Core.Entities;
using TCJ.EntityFrameworkCore.UnitOfWork;

namespace TCJ.EntityFrameworkCore.Repositories;

/// <summary>
/// Marks a repository contract.
/// </summary>
public interface IRepository;

/// <summary>
/// Combines read and write operations for an entity with a strongly typed key.
/// Transaction and persistence boundaries are provided separately by
/// <see cref="IUnitOfWork"/>.
/// </summary>
/// <typeparam name="TEntity">The entity type.</typeparam>
/// <typeparam name="TKey">The primary-key type.</typeparam>
public interface IRepository<TEntity, TKey> :
    IRepository,
    IReadRepository<TEntity, TKey>,
    IWriteRepository<TEntity, TKey>
    where TEntity : class, IEntity<TKey>;

/// <summary>
/// Repository contract for entities using <see cref="long"/> primary keys.
/// </summary>
/// <typeparam name="TEntity">The entity type.</typeparam>
public interface IRepository<TEntity> : IRepository<TEntity, long>
    where TEntity : class, IEntity<long>;
