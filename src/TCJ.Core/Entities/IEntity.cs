namespace TCJ.Core.Entities;

/// <summary>
/// Marks a domain entity.
/// </summary>
public interface IEntity;

/// <summary>
/// Represents a domain entity with a strongly typed key.
/// </summary>
/// <typeparam name="TKey">The key type.</typeparam>
public interface IEntity<out TKey> : IEntity, IHasKey<TKey>;

/// <summary>
/// Marks an entity data-transfer object.
/// </summary>
public interface IEntityDto;

/// <summary>
/// Represents an entity data-transfer object with a mutable strongly typed identifier.
/// </summary>
/// <typeparam name="TKey">The identifier type.</typeparam>
public interface IEntityDto<TKey> : IEntityDto
{
    /// <summary>
    /// Gets or sets the identifier.
    /// </summary>
    TKey Id { get; set; }
}
