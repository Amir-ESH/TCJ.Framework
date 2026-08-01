namespace TCJ.Core.Entities;

/// <summary>
/// Represents an entity that records creation and last-modification audit information.
/// </summary>
public interface IAuditedEntity
{
    /// <summary>
    /// Gets or sets the UTC instant at which the entity was created.
    /// </summary>
    DateTimeOffset? CreatedOn { get; set; }

    /// <summary>
    /// Gets or sets the UTC instant at which the entity was last modified.
    /// </summary>
    DateTimeOffset? ModifiedOn { get; set; }

    /// <summary>
    /// Gets or sets the identifier of the user that created the entity.
    /// </summary>
    long? CreatedBy { get; set; }

    /// <summary>
    /// Gets or sets the identifier of the user that last modified the entity.
    /// </summary>
    long? ModifiedBy { get; set; }
}
