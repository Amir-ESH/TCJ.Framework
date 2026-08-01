namespace TCJ.Core.Entities;

/// <summary>
/// Base class for audited entities with a database-managed binary concurrency token.
/// </summary>
/// <typeparam name="TKey">The key type.</typeparam>
public abstract class RowVersionAuditedEntity<TKey> : AuditedEntity<TKey>, IRowVersion
{
    /// <inheritdoc />
    public byte[] RowVersion { get; private set; } = [];
}

/// <summary>
/// Base class for audited entity DTOs with a binary concurrency token.
/// </summary>
/// <typeparam name="TKey">The identifier type.</typeparam>
public abstract class RowVersionAuditedEntityDto<TKey> : AuditedEntityDto<TKey>, IRowVersion
{
    /// <inheritdoc />
    public byte[] RowVersion { get; set; } = [];
}
