namespace TCJ.Core.Entities;

/// <summary>
/// Base class for fully audited, logically deletable entities with a database-managed binary concurrency token.
/// </summary>
/// <typeparam name="TKey">The key type.</typeparam>
public abstract class RowVersionFullAuditedEntity<TKey> : FullAuditedEntity<TKey>, IRowVersion
{
    /// <inheritdoc />
    public byte[] RowVersion { get; private set; } = [];
}

/// <summary>
/// Base class for fully audited entity DTOs with logical-deletion information and a binary concurrency token.
/// </summary>
/// <typeparam name="TKey">The identifier type.</typeparam>
public abstract class RowVersionFullAuditedEntityDto<TKey> : FullAuditedEntityDto<TKey>, IRowVersion
{
    /// <inheritdoc />
    public byte[] RowVersion { get; set; } = [];
}
