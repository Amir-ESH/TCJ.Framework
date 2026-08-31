# TCJ.EntityFrameworkCore

`TCJ.EntityFrameworkCore` adds provider-independent repositories, specifications, Unit of Work, auditing, soft delete, data seeding, entity searching, and persistence interceptors.

## Install

```bash
dotnet add package TCJ.EntityFrameworkCore --version 0.1.0-preview.3
```

- **Target framework:** `net10.0`
- **Main namespaces:** `TCJ.EntityFrameworkCore.Repositories`, `TCJ.EntityFrameworkCore.Specifications`, `TCJ.EntityFrameworkCore.UnitOfWork`, `TCJ.EntityFrameworkCore.Extensions`, `TCJ.EntityFrameworkCore.StrongTypes`
- **Primary entry points:** `AddTcjEntityFrameworkCore`, `IRepository<TEntity>`, `Specification<TEntity>`, and `IUnitOfWork`

```csharp
services.AddTcjEntityFrameworkCore<AppDbContext>(options =>
    options.UseInMemoryDatabase("app"));
```

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

## Strongly Typed ID conversions

Generated Strong IDs are registered explicitly; this package does not scan assemblies to discover them. Configure the model first, build a `StrongIdConversionRegistry`, and apply it:

```csharp
using TCJ.EntityFrameworkCore.Extensions;
using TCJ.EntityFrameworkCore.StrongTypes;

var strongIds = new StrongIdConversionRegistry()
    .Register<OrderId, Guid>(
        OrderId.StrongIdConversion.ToBackingValue,
        OrderId.StrongIdConversion.FromBackingValue)
    .Register<CustomerNumber, int>(
        CustomerNumber.StrongIdConversion.ToBackingValue,
        CustomerNumber.StrongIdConversion.FromBackingValue);

modelBuilder.ApplyStrongIdConversions(strongIds);
```

The registry supports generated `Guid`, `int`, and `long` IDs and applies their primitive conversions consistently to matching keys, foreign keys, nullable wrappers, and ordinary properties. Duplicate use of the same generated registration is idempotent; conflicting registrations or an already-configured different property converter fail explicitly. The registry is provider-neutral and does not add SQL Server behavior.

## Value Object conversions

Primitive-backed Value Objects use the same explicit registration model and do not require runtime assembly scanning:

```csharp
var valueObjects = new ValueObjectConversionRegistry()
    .Register<EmailAddress, string>(
        EmailAddress.ValueObjectConversion.ToBackingValue,
        EmailAddress.ValueObjectConversion.FromBackingValue)
    .Register<MoneyAmount, decimal>(
        MoneyAmount.ValueObjectConversion.ToBackingValue,
        MoneyAmount.ValueObjectConversion.FromBackingValue);

modelBuilder.ApplyValueObjectConversions(valueObjects);
```

Supported backing types are `string`, `Guid`, `int`, `long`, and `decimal`, and each Value Object is persisted as that primitive provider value. Materialization goes back through the generated `Create` path, including optional normalization and validation. Invalid legacy database values fail with an actionable `InvalidOperationException` that names the Value Object type without echoing the rejected value or application validation messages. TCJ does not bypass validation, rewrite invalid stored values, or add provider-specific domain behavior. Immutable record-struct equality is sufficient for EF change tracking, so no custom comparer is required.

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

## Transactional outbox

This package owns the provider-independent EF persistence, serialization, processor, startup-validation, and health components used by TCJ's optional transactional outbox. Applications must opt in explicitly and remain responsible for their database migration. See [Transactional outbox](../outbox.md).

## Health integration

See [Health checks and startup diagnostics](../health-checks.md) for the the health-check feature set APIs and operational contracts supported by this package.

Related guides: [Specifications and repositories](../guides/specifications-and-repositories.md), [auditing and soft delete](../guides/auditing-soft-delete-rowversion.md), [data seeding](../guides/data-seeding.md), [health checks](../health-checks.md), [transactional outbox](../outbox.md), and the [generated API reference](../api/index.md).
