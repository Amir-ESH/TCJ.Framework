# TCJ.EntityFrameworkCore.SqlServer

This package combines SQL Server provider configuration with all services from `TCJ.EntityFrameworkCore`.

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
