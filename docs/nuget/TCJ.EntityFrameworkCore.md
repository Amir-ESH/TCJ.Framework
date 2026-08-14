# TCJ.EntityFrameworkCore

`TCJ.EntityFrameworkCore` provides provider-independent EF Core infrastructure for TCJ applications, including repositories, specifications, Unit of Work, auditing, soft delete, ordered/idempotent seeding, entity metadata, health integration, and transactional-outbox persistence components.

## Install

```bash
dotnet add package TCJ.EntityFrameworkCore --prerelease
```

TCJ Framework is currently pre-1.0. Pin the exact preview version used by your application when reproducibility matters.

## Highlights

- Repository and specification abstractions over EF Core.
- Unit of Work and transaction boundaries without hidden `SaveChanges` calls.
- Auditing and explicit soft-delete behavior.
- Ordered, idempotent data seeding support.
- Provider-independent health and startup-diagnostic integration.
- Transactional-outbox persistence and processing infrastructure.
- Experimental Native AOT support following EF Core's compiled-model/query-precompilation constraints.

## Example

```csharp
using TCJ.EntityFrameworkCore.Extensions;

services.AddTcjEntityFrameworkCore<AppDbContext>(options =>
    options.UseInMemoryDatabase("app"));
```

Production applications should configure the EF Core provider appropriate to their environment. SQL Server-specific registration is provided by `TCJ.EntityFrameworkCore.SqlServer`.

## Documentation

- [TCJ.EntityFrameworkCore package documentation](https://github.com/Amir-ESH/TCJ.Framework/blob/main/docs/packages/tcj-entityframeworkcore.md)
- [Specifications and repositories guide](https://github.com/Amir-ESH/TCJ.Framework/blob/main/docs/guides/specifications-and-repositories.md)
- [Auditing, soft delete, and rowversion guide](https://github.com/Amir-ESH/TCJ.Framework/blob/main/docs/guides/auditing-soft-delete-rowversion.md)
- [Data seeding guide](https://github.com/Amir-ESH/TCJ.Framework/blob/main/docs/guides/data-seeding.md)
- [Transactional outbox](https://github.com/Amir-ESH/TCJ.Framework/blob/main/docs/outbox.md)
- [Native AOT and trimming guide](https://github.com/Amir-ESH/TCJ.Framework/blob/main/docs/guides/native-aot-and-trimming.md)
- [Repository](https://github.com/Amir-ESH/TCJ.Framework)
- [Issues](https://github.com/Amir-ESH/TCJ.Framework/issues)

## License

TCJ Framework is licensed under GNU LGPL v3.0 only (`LGPL-3.0-only`). See the repository license for details.
