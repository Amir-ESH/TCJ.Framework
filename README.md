# TCJ Framework

TCJ Framework is a modular foundation for building .NET 10 applications with explicit boundaries between domain primitives, dependency injection, persistence, SQL Server, and ASP.NET Core integration.

> **Status:** `0.1.0-preview.1` is under active development. Public APIs may change before `1.0.0`, and the NuGet packages have not been published yet.

- Repository: <https://github.com/Amir-ESH/TCJ.Framework>
- Documentation: <https://github.com/Amir-ESH/TCJ.Framework/tree/main/docs>
- Product API sample: <https://github.com/Amir-ESH/TCJ.Framework/tree/main/samples/TCJ.Empty>
- License: [MIT](https://github.com/Amir-ESH/TCJ.Framework/blob/main/LICENSE.txt)

## Packages

| Package | Purpose |
| --- | --- |
| `TCJ.Core` | Entities, Result pattern, domain-event contracts, guards, identifiers, and security abstractions. |
| `TCJ.DependencyInjection` | Convention-based service registration and sequential domain-event dispatching. |
| `TCJ.EntityFrameworkCore` | Repositories, specifications, unit of work, auditing, soft delete, seeding, and entity metadata search. |
| `TCJ.EntityFrameworkCore.SqlServer` | SQL Server registration options and rowversion conventions. |
| `TCJ.AspNetCore` | Current-user resolution, Result-to-HTTP mapping, Problem Details, and centralized exception handling. |

## Requirements

- .NET SDK `10.0.100` or a compatible SDK selected by [`global.json`](https://github.com/Amir-ESH/TCJ.Framework/blob/main/global.json)
- SQL Server only when using `TCJ.EntityFrameworkCore.SqlServer` or running the sample application

## Quick start from source

The preview packages are not published yet. Clone the repository and use the included sample or project references during development:

```bash
git clone https://github.com/Amir-ESH/TCJ.Framework.git
cd TCJ.Framework
dotnet restore TCJ.slnx
dotnet build TCJ.slnx -c Release --no-restore
dotnet test TCJ.slnx -c Release --no-build
```

Run the Product API sample:

```bash
dotnet run --project samples/TCJ.Empty/TCJ.Empty.csproj
```

The default Development connection string uses SQL Server LocalDB on Windows. See the [sample README](https://github.com/Amir-ESH/TCJ.Framework/blob/main/samples/TCJ.Empty/README.md) for configuration details.

## Minimal application setup

```csharp
using TCJ.AspNetCore.Extensions;
using TCJ.DependencyInjection.Extensions;
using TCJ.EntityFrameworkCore.SqlServer.Extensions;

var builder = WebApplication.CreateBuilder(args);

string connectionString = builder.Configuration
    .GetConnectionString("Default")
    ?? throw new InvalidOperationException(
        "Connection string 'Default' was not found.");

builder.Services.AddTcjDependencyInjection(typeof(Program).Assembly);
builder.Services.AddTcjAspNetCore();
builder.Services.AddTcjSqlServer<AppDbContext>(connectionString);

var app = builder.Build();

app.UseTcjAspNetCore();
app.Run();
```

Your `DbContext` must implement `IReadDbContext` and `IWriteDbContext`:

```csharp
public sealed class AppDbContext(DbContextOptions<AppDbContext> options)
    : DbContext(options), IReadDbContext, IWriteDbContext
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.ApplySoftDeleteQueryFilters();
        modelBuilder.ApplyTcjSqlServerConventions();
    }
}
```

## Build, test, and pack

```bash
dotnet restore TCJ.slnx
dotnet build TCJ.slnx -c Release --no-restore
dotnet test TCJ.slnx -c Release --no-build
dotnet pack TCJ.slnx -c Release --no-build
```

NuGet packages and symbol packages are written to `artifacts/packages`.

## Documentation map

- [Getting started](https://github.com/Amir-ESH/TCJ.Framework/blob/main/docs/getting-started.md)
- [Architecture and package boundaries](https://github.com/Amir-ESH/TCJ.Framework/blob/main/docs/architecture.md)
- [Package reference](https://github.com/Amir-ESH/TCJ.Framework/blob/main/docs/README.md#package-reference)
- [Guides and recipes](https://github.com/Amir-ESH/TCJ.Framework/blob/main/docs/README.md#guides)
- [Development workflow](https://github.com/Amir-ESH/TCJ.Framework/blob/main/docs/development.md)
- [Versioning and releases](https://github.com/Amir-ESH/TCJ.Framework/blob/main/docs/versioning.md)
- [Contributing](https://github.com/Amir-ESH/TCJ.Framework/blob/main/CONTRIBUTING.md)
- [Security policy](https://github.com/Amir-ESH/TCJ.Framework/blob/main/SECURITY.md)
- [Support](https://github.com/Amir-ESH/TCJ.Framework/blob/main/SUPPORT.md)
- [Changelog](https://github.com/Amir-ESH/TCJ.Framework/blob/main/CHANGELOG.md)

## Contributing

Contributions are welcome through focused issues and pull requests. Read [`CONTRIBUTING.md`](https://github.com/Amir-ESH/TCJ.Framework/blob/main/CONTRIBUTING.md) before opening a change.

## License

TCJ Framework is licensed under the [MIT License](https://github.com/Amir-ESH/TCJ.Framework/blob/main/LICENSE.txt).
