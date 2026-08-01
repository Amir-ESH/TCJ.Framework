namespace TCJ.Core.Entities;

/// <summary>
/// Base class for audited entities that also support logical deletion.
/// </summary>
/// <typeparam name="TKey">The key type.</typeparam>
public abstract class FullAuditedEntity<TKey> : AuditedEntity<TKey>, ISoftDelete
{
    /// <inheritdoc />
    public bool IsDeleted { get; set; }

    /// <inheritdoc />
    public DateTimeOffset? DeletedOn { get; set; }

    /// <inheritdoc />
    public long? DeletedBy { get; set; }
}

/// <summary>
/// Base class for audited entity DTOs that also expose logical-deletion information.
/// </summary>
/// <typeparam name="TKey">The identifier type.</typeparam>
public abstract class FullAuditedEntityDto<TKey> : AuditedEntityDto<TKey>, ISoftDelete
{
    /// <inheritdoc />
    public bool IsDeleted { get; set; }

    /// <inheritdoc />
    public DateTimeOffset? DeletedOn { get; set; }

    /// <inheritdoc />
    public long? DeletedBy { get; set; }
}
