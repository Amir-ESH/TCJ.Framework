namespace TCJ.Core.Entities;

/// <summary>
/// Exposes an entity key without requiring the caller to know its concrete type.
/// </summary>
public interface IHasKey
{
    /// <summary>
    /// Gets the entity key as an object.
    /// </summary>
    object? GetKey();
}

/// <summary>
/// Exposes a strongly typed entity key.
/// </summary>
/// <typeparam name="TKey">The key type.</typeparam>
public interface IHasKey<out TKey> : IHasKey
{
    /// <summary>
    /// Gets the strongly typed entity key.
    /// </summary>
    TKey Id { get; }
}
