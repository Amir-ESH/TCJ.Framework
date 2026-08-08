# TCJ.EntityFrameworkCore

This package supplies provider-neutral EF Core infrastructure for contexts implementing `IReadDbContext` and `IWriteDbContext`.

## Registration

Configure a context directly:

```csharp
builder.Services.AddTcjEntityFrameworkCore<AppDbContext>(options =>
{
    options.UseSqlite(connectionString);
});
```

Or register a context separately and then register TCJ abstractions:

```csharp
builder.Services.AddDbContext<AppDbContext>((serviceProvider, options) =>
{
    options.UseSomeProvider(connectionString);
    options.AddTcjPersistenceInterceptors(serviceProvider);
});

builder.Services.AddTcjEntityFrameworkCore<AppDbContext>();
```

When the context is registered separately, calling `AddTcjPersistenceInterceptors` is required for auditing.

## Registered services

- `IReadDbContext`
- `IWriteDbContext`
- read, write, combined, and soft-delete repositories
- `IUnitOfWork`
- `AuditingSaveChangesInterceptor`
- `IDataSeeder`
- `IEntitySearcher`
- `TimeProvider.System` when no replacement exists

## Repository behavior

Read operations are no-tracking by default. `TrackedQuery` and specifications using `AsTracking` are intended for update workflows.

Write repository methods stage changes only. Call `IUnitOfWork.SaveChangesAsync` to persist them.

`IWriteRepository.Remove` means physical deletion. Use `ISoftDeleteRepository.SoftDelete` for logical deletion.

## Specifications

Specifications can compose:

- filter criteria
- includes
- primary and secondary ordering
- offset pagination
- tracking behavior
- global query-filter bypass
- split-query execution

Paging requires deterministic ordering; the evaluator rejects paging specifications that do not define an order.

## Auditing

The save interceptor updates properties on entities implementing `IAuditedEntity` and `ISoftDelete`, using `TimeProvider` and `ICurrentUserProvider` when available.

## Soft delete

Call `ApplySoftDeleteQueryFilters` in `OnModelCreating`. Deleted rows are then excluded from normal queries. Specifications can use `IgnoreGlobalQueryFilters` for administrative or restore workflows.

## Data seeding and entity search

`IDataSeedContributor` implementations run in deterministic order through `IDataSeeder`. `IEntitySearcher` uses EF Core model metadata to test record existence from string property or primary-key values.
