# TCJ.EntityFrameworkCore

`TCJ.EntityFrameworkCore` adds provider-independent repositories, specifications, Unit of Work, auditing, soft delete, data seeding, entity searching, and persistence interceptors.

## Install

```bash
dotnet add package TCJ.EntityFrameworkCore --version 0.1.0-preview.2
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
