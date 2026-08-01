# Auditing, soft delete, and rowversion

## Audited entity choices

```csharp
public sealed class Product : FullAuditedEntity<Guid>
{
    // Domain members
}
```

`FullAuditedEntity<TKey>` exposes:

```text
CreatedOn
CreatedBy
ModifiedOn
ModifiedBy
IsDeleted
DeletedOn
DeletedBy
```

Use `RowVersionFullAuditedEntity<TKey>` when SQL Server optimistic concurrency is also required.

## Register auditing

The simplest path is `AddTcjSqlServer` or a configured `AddTcjEntityFrameworkCore` overload. Both attach `AuditingSaveChangesInterceptor`.

When the `DbContext` is registered manually, add the interceptor explicitly:

```csharp
builder.Services.AddDbContext<AppDbContext>((serviceProvider, options) =>
{
    options.UseSqlServer(connectionString);
    options.AddTcjPersistenceInterceptors(serviceProvider);
});

builder.Services.AddTcjEntityFrameworkCore<AppDbContext>();
```

Audit timestamps come from `TimeProvider`. User identifiers come from `ICurrentUserProvider`; null represents unauthenticated or system work.

## Configure soft-delete filtering

```csharp
protected override void OnModelCreating(ModelBuilder modelBuilder)
{
    base.OnModelCreating(modelBuilder);
    modelBuilder.ApplySoftDeleteQueryFilters();
}
```

## Soft-delete and restore

```csharp
softDeleteRepository.SoftDelete(product);
await unitOfWork.SaveChangesAsync(cancellationToken);
```

```csharp
softDeleteRepository.Restore(product);
await unitOfWork.SaveChangesAsync(cancellationToken);
```

`IWriteRepository.Remove` is a physical delete and does not switch to logical deletion automatically.

## Configure SQL Server rowversion

```csharp
protected override void OnModelCreating(ModelBuilder modelBuilder)
{
    base.OnModelCreating(modelBuilder);
    modelBuilder.ApplyTcjSqlServerConventions();
}
```

The convention configures mapped `IRowVersion.RowVersion` properties as required, database-generated concurrency tokens. Consumers remain responsible for deciding how `DbUpdateConcurrencyException` is translated into application behavior.
