namespace TCJ.Core.Entities;

/// <summary>
/// Represents an entity that supports logical deletion.
/// </summary>
public interface ISoftDelete
{
    /// <summary>
    /// Gets or sets whether the entity is logically deleted.
    /// </summary>
    bool IsDeleted { get; set; }

    /// <summary>
    /// Gets or sets the UTC instant at which the entity was logically deleted.
    /// </summary>
    DateTimeOffset? DeletedOn { get; set; }

    /// <summary>
    /// Gets or sets the identifier of the user that logically deleted the entity.
    /// </summary>
    long? DeletedBy { get; set; }
}
