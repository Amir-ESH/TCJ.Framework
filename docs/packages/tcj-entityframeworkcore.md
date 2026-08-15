# TCJ.EntityFrameworkCore

`TCJ.EntityFrameworkCore` adds provider-independent repositories, specifications, Unit of Work, auditing, soft delete, data seeding, entity searching, and persistence interceptors.

## Install

```bash
dotnet add package TCJ.EntityFrameworkCore --version 0.1.0-preview.3
```

- **Target framework:** `net10.0`
- **Main namespaces:** `TCJ.EntityFrameworkCore.Repositories`, `TCJ.EntityFrameworkCore.Specifications`, `TCJ.EntityFrameworkCore.UnitOfWork`, `TCJ.EntityFrameworkCore.Extensions`
- **Primary entry points:** `AddTcjEntityFrameworkCore`, `IRepository<TEntity>`, `Specification<TEntity>`, and `IUnitOfWork`

```csharp
services.AddTcjEntityFrameworkCore<AppDbContext>(options =>
    options.UseInMemoryDatabase("app"));
```

Related guides: [Specifications and repositories](../guides/specifications-and-repositories.md), [auditing and soft delete](../guides/auditing-soft-delete-rowversion.md), and the [generated API reference](../api/index.md).

## Health integration

See [Health checks and startup diagnostics](../health-checks.md) for the Step 43 APIs and operational contracts supported by this package.

## Native AOT (experimental)

`TCJ.EntityFrameworkCore` has an **Experimental** NativeAOT support tier. This is intentionally not a production-support claim: EF Core NativeAOT and query precompilation remain upstream-experimental, and a real application must use EF's compiled-model and precompiled-query tooling.

For EF Core 10, a NativeAOT application must enable `PublishAot`, use a concrete runtime identifier, reference `Microsoft.EntityFrameworkCore.Tasks`, opt into `Microsoft.EntityFrameworkCore.GeneratedInterceptors`, and generate the compiled model and precompiled queries during publish. See [Native AOT and trimming compatibility](../guides/native-aot-and-trimming.md) for the exact project settings and the repository fixture.

The supported experiment is the **static** model/query path. Do not use these runtime-discovery APIs in that experiment:

- `RegisterEntityTypeConfiguration(...)`
- `RegisterAllEntities<TBaseType>(...)`
- `GetModuleAssemblies()`
- `ApplySoftDeleteQueryFilters()` (unsupported on the current compiled-model NativeAOT path because EF compiled models do not support global query filters)
- `IEntitySearcher.ExistsAsync(...)` / `FindAsync(...)`

The model-discovery and entity-search APIs carry trimming/dynamic-code annotations so AOT consumers get an actionable compiler diagnostic. Prefer explicit generic model configuration and statically typed repository/`DbContext` queries that EF can precompile. TCJ soft delete remains fully available to normal JIT consumers; it is excluded only from this NativeAOT experiment because it depends on EF global query filters.

Transactional outbox is outside the Important 7 NativeAOT fixture. If experimenting with it, explicitly register persisted event contracts with `AddTcjOutboxEvent<TEvent>` and supply `System.Text.Json` metadata for every event payload type; do not depend on convention fallback assembly scanning.

Normal JIT consumers are unchanged: compiled models, precompiled-query tooling, and `PublishAot` are not required for normal package use.
