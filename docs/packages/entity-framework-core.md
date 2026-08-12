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

## Native AOT (experimental)

NativeAOT support for `TCJ.EntityFrameworkCore` is **Experimental**. EF Core requires a compiled model and precompiled queries for the NativeAOT path, and current upstream support remains experimental.

Use explicit model configuration and statically typed LINQ queries that EF tooling can discover. The following TCJ runtime-discovery paths are restricted for NativeAOT and carry trimming/dynamic-code annotations where applicable: `RegisterEntityTypeConfiguration(...)`, `RegisterAllEntities<TBaseType>(...)`, `GetModuleAssemblies()`, and `IEntitySearcher.ExistsAsync(...)` / `FindAsync(...)`. `ApplySoftDeleteQueryFilters()` is also outside the current NativeAOT experiment because EF compiled models do not support global query filters; normal JIT soft-delete usage is unchanged.

The dedicated project-reference fixture `tests/TCJ.EntityFrameworkCore.NativeAotExperimental` documents and verifies the EF Core 10 publish prerequisites (`Microsoft.EntityFrameworkCore.Tasks`, compiled-model generation, query precompilation, generated interceptors, and a concrete RID). See [Native AOT and trimming compatibility](../guides/native-aot-and-trimming.md) for the exact boundary.

Normal JIT consumers are unaffected and do not need NativeAOT tooling or compiled models.
