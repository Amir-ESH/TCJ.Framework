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

## Native AOT (experimental)

`TCJ.EntityFrameworkCore.SqlServer` is separately classified as **Experimental** for NativeAOT. The repository experiment uses the SQL Server provider, EF Core compiled-model/query-precompile tooling, and `ApplyTcjSqlServerConventions()` in a project-reference NativeAOT fixture.

TCJ's rowversion convention configures the finalized model through EF metadata rather than reopening entity builders from runtime `Type` values. This keeps the TCJ-owned provider convention on the static experiment path, but it does **not** override EF Core's upstream NativeAOT status: provider participation in precompiled-query support remains an EF/provider capability boundary.

The current fixture proves only project-reference publish/startup behavior and does not connect to SQL Server. It is not a packaged production guarantee. A future tier upgrade requires a real packaged consumer publish-and-execute scenario as defined by the repository AOT policy. The provider path also inherits the provider-neutral compiled-model limitations; in particular, TCJ soft-delete global query filters are outside the current NativeAOT experiment.

Normal SQL Server/JIT applications continue to use `AddTcjSqlServer<TDbContext>` normally and do not need NativeAOT tooling or compiled models.
