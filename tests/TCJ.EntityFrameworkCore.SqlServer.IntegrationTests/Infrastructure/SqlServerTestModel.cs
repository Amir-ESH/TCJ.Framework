using TCJ.Core.DomainEvents;
using TCJ.Core.Entities;
using TCJ.Core.StrongTypes;

namespace TCJ.EntityFrameworkCore.SqlServer.IntegrationTests.Infrastructure;

internal sealed class SqlServerTestEntity : RowVersionFullAuditedEntity<Guid>
{
    private SqlServerTestEntity()
    {
    }

    public SqlServerTestEntity(Guid id, string name, decimal amount, DateTimeOffset occurredOn)
        : base()
    {
        Id = id;
        Name = name;
        Amount = amount;
        OccurredOn = occurredOn;
    }

    public string Name { get; set; } = string.Empty;

    public decimal Amount { get; set; }

    public DateTimeOffset OccurredOn { get; set; }

    public string? OptionalText { get; set; }

    public void RaisePersistenceMarker() => AddDomainEvent(new SqlServerPersistenceMarker(Id, DateTimeOffset.UtcNow));
}

internal sealed record SqlServerPersistenceMarker(Guid EntityId, DateTimeOffset OccurredOn) : IDomainEvent;

internal sealed class SqlServerParent : Entity<int>
{
    private SqlServerParent()
    {
    }

    public SqlServerParent(string name)
    {
        Name = name;
    }

    public string Name { get; set; } = string.Empty;

    public ICollection<SqlServerChild> Children { get; } = new List<SqlServerChild>();
}

internal sealed class SqlServerChild : Entity<int>
{
    private SqlServerChild()
    {
    }

    public SqlServerChild(string name, SqlServerParent parent)
    {
        Name = name;
        Parent = parent;
    }

    public string Name { get; set; } = string.Empty;

    public int ParentId { get; private set; }

    public SqlServerParent Parent { get; private set; } = null!;
}

[StronglyTypedId<Guid>]
internal readonly partial record struct SqlServerStrongGuidId;

[StronglyTypedId<int>]
internal readonly partial record struct SqlServerStrongIntId;

[StronglyTypedId<long>]
internal readonly partial record struct SqlServerStrongLongId;

internal sealed class SqlServerStrongIdRecord
{
    private SqlServerStrongIdRecord()
    {
    }

    public SqlServerStrongIdRecord(
        SqlServerStrongGuidId id,
        SqlServerStrongIntId intId,
        SqlServerStrongLongId longId,
        SqlServerStrongGuidId? optionalGuidId = null)
    {
        Id = id;
        IntId = intId;
        LongId = longId;
        OptionalGuidId = optionalGuidId;
    }

    public SqlServerStrongGuidId Id { get; private set; }

    public SqlServerStrongIntId IntId { get; private set; }

    public SqlServerStrongLongId LongId { get; private set; }

    public SqlServerStrongGuidId? OptionalGuidId { get; private set; }
}

