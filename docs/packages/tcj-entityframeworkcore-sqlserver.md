# TCJ.EntityFrameworkCore.SqlServer

`TCJ.EntityFrameworkCore.SqlServer` connects the provider-independent persistence abstractions to Microsoft SQL Server and exposes TCJ-specific SQL Server options.

## Install

```bash
dotnet add package TCJ.EntityFrameworkCore.SqlServer --version 0.1.0-preview.3
```

- **Target framework:** `net10.0`
- **Main namespaces:** `TCJ.EntityFrameworkCore.SqlServer.Extensions`, `TCJ.EntityFrameworkCore.SqlServer.Options`
- **Primary entry points:** `AddTcjSqlServer<TDbContext>` and `TcjSqlServerOptions`

```csharp
services.AddTcjSqlServer<AppDbContext>(connectionString);
```

## Registration

```csharp
builder.Services.AddTcjSqlServer<AppDbContext>(
    connectionString,
    configureTcjSqlServer: options =>
    {
        options.EnableRetryOnFailure = true;
        options.MaxRetryCount = 6;
        options.MaxRetryDelay = TimeSpan.FromSeconds(30);
        options.CommandTimeout = 30;
        options.MigrationsAssembly = typeof(AppDbContext).Assembly.FullName;
        options.AdditionalTransientErrorNumbers.Add(49999);
    });
```

A connection-string factory overload is available when the value must be resolved from the service provider.

## Defaults

- Retry on failure: enabled
- Maximum retry count: `6`
- Maximum retry delay: `30 seconds`
- Command timeout: provider default
- Migrations assembly: provider default

Invalid retry counts, retry delays, or command timeouts fail during configuration.

## Model conventions

Call this in `OnModelCreating`:

```csharp
modelBuilder.ApplyTcjSqlServerConventions();
```

The convention locates mapped properties corresponding to `IRowVersion.RowVersion` and configures them as required, database-generated concurrency tokens using SQL Server rowversion semantics.

## Transactions and retry strategies

When retry-on-failure is enabled, application-created transactions may require execution-strategy orchestration. The current sample disables retry because its data seeder owns an explicit transaction. Do not copy that sample setting blindly into production; choose retry and transaction behavior together.

## Native AOT (experimental)

The SQL Server NativeAOT path is separately **Experimental**. The dedicated project-reference fixture configures SQL Server, applies TCJ rowversion conventions, and relies on EF Core's compiled-model/query-precompile MSBuild tooling during NativeAOT publish.

TCJ does not turn this into a production-support claim: EF provider participation remains an upstream capability boundary, and the SQL Server path inherits the provider-neutral compiled-model limitations (including the exclusion of TCJ soft-delete global query filters). A future support-tier upgrade requires packaged-consumer execution evidence. Normal SQL Server/JIT consumers continue to use `AddTcjSqlServer<TDbContext>` without any NativeAOT-specific setup.

## Transactional outbox

SQL Server-specific claiming, lease handling, and status updates are provided through the opt-in SQL Server outbox registration. The consumer owns the `TCJ_OutboxMessages` migration. See [Transactional outbox](../outbox.md).

## Health integration

See [Health checks and startup diagnostics](../health-checks.md) for the the health-check feature set APIs and operational contracts supported by this package.

Related packages: [TCJ.EntityFrameworkCore](tcj-entityframeworkcore.md) and [TCJ.DependencyInjection](tcj-dependencyinjection.md). See [SQL Server integration testing](../sqlserver-integration-testing.md), [health checks](../health-checks.md), [resilience](../resilience.md), [transactional outbox](../outbox.md), and the [generated API reference](../api/index.md).
