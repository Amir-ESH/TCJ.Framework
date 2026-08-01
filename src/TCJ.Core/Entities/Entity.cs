using TCJ.Core.DomainEvents;

namespace TCJ.Core.Entities;

/// <summary>
/// Base class for domain entities with a strongly typed key and domain-event support.
/// </summary>
/// <typeparam name="TKey">The key type.</typeparam>
public abstract class Entity<TKey> : IEntity<TKey>, IHasDomainEvents
{
    private readonly List<IDomainEvent> _domainEvents = [];

    /// <summary>
    /// Initializes a new entity. This constructor is intended for serializers and ORMs.
    /// </summary>
    protected Entity() { }

    /// <summary>
    /// Initializes a new entity with the specified key.
    /// </summary>
    /// <param name="id">The entity key.</param>
    protected Entity(TKey id)
    {
        Id = id;
    }

    /// <summary>
    /// Gets the entity key.
    /// </summary>
    public virtual TKey Id { get; protected set; } = default!;

    /// <inheritdoc />
    public IReadOnlyCollection<IDomainEvent> DomainEvents => _domainEvents;

    /// <inheritdoc />
    public object? GetKey() => Id;

    /// <summary>
    /// Adds a domain event to the pending collection.
    /// </summary>
    /// <param name="domainEvent">The domain event to add.</param>
    protected void AddDomainEvent(IDomainEvent domainEvent)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        _domainEvents.Add(domainEvent);
    }

    /// <summary>
    /// Removes a pending domain event.
    /// </summary>
    /// <param name="domainEvent">The domain event to remove.</param>
    /// <returns><see langword="true"/> when the event was removed.</returns>
    protected bool RemoveDomainEvent(IDomainEvent domainEvent)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        return _domainEvents.Remove(domainEvent);
    }

    /// <inheritdoc />
    public void ClearDomainEvents() => _domainEvents.Clear();
}

/// <summary>
/// Base class for entity data-transfer objects with a mutable strongly typed identifier.
/// </summary>
/// <typeparam name="TKey">The identifier type.</typeparam>
public abstract class EntityDto<TKey> : IEntityDto<TKey>
{
    /// <inheritdoc />
    public TKey Id { get; set; } = default!;
}
