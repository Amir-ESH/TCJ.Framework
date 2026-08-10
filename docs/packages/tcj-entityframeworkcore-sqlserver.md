# TCJ.EntityFrameworkCore.SqlServer

`TCJ.EntityFrameworkCore.SqlServer` connects the provider-independent persistence abstractions to Microsoft SQL Server and exposes TCJ-specific SQL Server options.

## Install

```bash
dotnet add package TCJ.EntityFrameworkCore.SqlServer --version 0.1.0-preview.2
```

- **Target framework:** `net10.0`
- **Main namespaces:** `TCJ.EntityFrameworkCore.SqlServer.Extensions`, `TCJ.EntityFrameworkCore.SqlServer.Options`
- **Primary entry points:** `AddTcjSqlServer<TDbContext>` and `TcjSqlServerOptions`

```csharp
services.AddTcjSqlServer<AppDbContext>(connectionString);
```

Related packages: [TCJ.EntityFrameworkCore](tcj-entityframeworkcore.md) and [TCJ.DependencyInjection](tcj-dependencyinjection.md). See the [generated API reference](../api/index.md).

## Health integration

See [Health checks and startup diagnostics](../health-checks.md) for the Step 43 APIs and operational contracts supported by this package.
