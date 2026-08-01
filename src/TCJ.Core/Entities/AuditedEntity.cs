namespace TCJ.Core.Entities;

/// <summary>
/// Base class for entities that record creation and last-modification audit information.
/// </summary>
/// <typeparam name="TKey">The key type.</typeparam>
public abstract class AuditedEntity<TKey> : Entity<TKey>, IAuditedEntity
{
    /// <inheritdoc />
    public DateTimeOffset? CreatedOn { get; set; }

    /// <inheritdoc />
    public DateTimeOffset? ModifiedOn { get; set; }

    /// <inheritdoc />
    public long? CreatedBy { get; set; }

    /// <inheritdoc />
    public long? ModifiedBy { get; set; }
}

/// <summary>
/// Base class for entity DTOs that expose creation and last-modification audit information.
/// </summary>
/// <typeparam name="TKey">The identifier type.</typeparam>
public abstract class AuditedEntityDto<TKey> : EntityDto<TKey>, IAuditedEntity
{
    /// <inheritdoc />
    public DateTimeOffset? CreatedOn { get; set; }

    /// <inheritdoc />
    public DateTimeOffset? ModifiedOn { get; set; }

    /// <inheritdoc />
    public long? CreatedBy { get; set; }

    /// <inheritdoc />
    public long? ModifiedBy { get; set; }
}
