# TCJ.EntityFrameworkCore.SqlServer

`TCJ.EntityFrameworkCore.SqlServer` connects TCJ's provider-independent EF Core infrastructure to Microsoft SQL Server. It provides SQL Server registration, resilient provider options, rowversion conventions, SQL Server health checks, and provider-specific transactional-outbox behavior.

## Install

```bash
dotnet add package TCJ.EntityFrameworkCore.SqlServer --prerelease
```

TCJ Framework is currently pre-1.0. Pin the exact preview version used by your application when reproducibility matters.

## Highlights

- `AddTcjSqlServer<TDbContext>` registration for SQL Server-backed applications.
- Bounded SQL Server retry/execution-strategy configuration.
- Rowversion conventions for optimistic concurrency.
- SQL Server connectivity and migration-readiness health checks.
- Provider-specific transactional-outbox claiming and processing behavior.
- Experimental Native AOT support, subject to EF Core and SQL Server provider limitations.

## Example

```csharp
using TCJ.EntityFrameworkCore.SqlServer.Extensions;

services.AddTcjSqlServer<AppDbContext>(connectionString);
```

## Dependencies

This package builds on `TCJ.Core` and `TCJ.EntityFrameworkCore` and uses the Microsoft SQL Server EF Core provider.

## Documentation

- [TCJ.EntityFrameworkCore.SqlServer package documentation](https://github.com/Amir-ESH/TCJ.Framework/blob/v0.1.0-preview.5/docs/packages/tcj-entityframeworkcore-sqlserver.md)
- [Auditing, soft delete, and rowversion guide](https://github.com/Amir-ESH/TCJ.Framework/blob/v0.1.0-preview.5/docs/guides/auditing-soft-delete-rowversion.md)
- [SQL Server integration testing](https://github.com/Amir-ESH/TCJ.Framework/blob/v0.1.0-preview.5/docs/sqlserver-integration-testing.md)
- [Resilience](https://github.com/Amir-ESH/TCJ.Framework/blob/v0.1.0-preview.5/docs/resilience.md)
- [Transactional outbox](https://github.com/Amir-ESH/TCJ.Framework/blob/v0.1.0-preview.5/docs/outbox.md)
- [Repository](https://github.com/Amir-ESH/TCJ.Framework)
- [Issues](https://github.com/Amir-ESH/TCJ.Framework/issues)

## License

TCJ Framework is licensed under GNU LGPL v3.0 only (`LGPL-3.0-only`). See the repository license for details.
