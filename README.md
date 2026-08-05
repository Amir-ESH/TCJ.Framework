# TCJ Framework

> **Latest published preview:** `0.1.0-preview.1`
> **Current development version:** `0.1.0-preview.2`

[![CI](https://github.com/Amir-ESH/TCJ.Framework/actions/workflows/ci.yml/badge.svg?branch=develop)](https://github.com/Amir-ESH/TCJ.Framework/actions/workflows/ci.yml)

TCJ Framework is a modular foundation for building .NET 10 applications with explicit boundaries between domain primitives, dependency injection, persistence, SQL Server, and ASP.NET Core integration.

> **Status:** `0.1.0-preview.1` is the first public preview. Public APIs may change before `1.0.0`; pin the exact preview version in production-like environments.

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

## Install the preview packages

Install only the modules required by the application:

```bash
dotnet add package TCJ.Core --version 0.1.0-preview.1
dotnet add package TCJ.DependencyInjection --version 0.1.0-preview.1
dotnet add package TCJ.EntityFrameworkCore --version 0.1.0-preview.1
dotnet add package TCJ.EntityFrameworkCore.SqlServer --version 0.1.0-preview.1
dotnet add package TCJ.AspNetCore --version 0.1.0-preview.1
```

## Quick start from source

Clone the repository to build, test, or run the included sample:

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
dotnet test TCJ.slnx -c Release --no-build \
  --collect:"XPlat Code Coverage" \
  --settings tests/coverlet.runsettings \
  --results-directory TestResults
python3 eng/verify-coverage.py verify
python3 eng/verify-mutation-results.py validate-config
python3 eng/verify-performance-results.py validate-config
python3 eng/verify-architecture-policy.py validate-config
python3 eng/verify-sbom.py validate-config
python3 eng/verify-reproducible-build.py validate-config
dotnet pack TCJ.slnx -c Release --no-build
```

NuGet packages and symbol packages are written to `artifacts/packages`. Restore audits direct and transitive dependencies, and Pack validates binary compatibility against the latest published TCJ packages. CI enforces line and branch coverage, while the dedicated **Mutation testing** workflow validates test effectiveness for the controlled `TCJ.Core` and `TCJ.DependencyInjection` scope. The dedicated **Performance benchmarks** workflow uses BenchmarkDotNet to record runtime and managed-allocation data and enforces only within-run ratios, avoiding unreliable absolute-time comparisons between different hosted runners. The solution also contains deterministic architecture tests that enforce the approved package graph, namespace ownership, and public API infrastructure boundaries. The first valid mutation run creates a candidate that must be reviewed and accepted before the recorded baseline can pass normal verification. CI also generates and validates a CycloneDX JSON software bill of materials, includes it in `artifacts/release/SHA256SUMS`, and uploads the SBOM summary. The dedicated **Reproducible builds** workflow creates two isolated package builds and compares assemblies, portable PDBs, Source Link, XML documentation, NuSpec metadata, source files, and extracted package contents. Release preflight and official tagged releases promote only a verified package set before SBOM generation, checksums, attestation, NuGet publication, and GitHub Release creation.

## Documentation map

- [Getting started](https://github.com/Amir-ESH/TCJ.Framework/blob/main/docs/getting-started.md)
- [Architecture and package boundaries](https://github.com/Amir-ESH/TCJ.Framework/blob/main/docs/architecture.md)
- [Package reference](https://github.com/Amir-ESH/TCJ.Framework/blob/main/docs/README.md#package-reference)
- [Guides and recipes](https://github.com/Amir-ESH/TCJ.Framework/blob/main/docs/README.md#guides)
- [Development workflow](https://github.com/Amir-ESH/TCJ.Framework/blob/main/docs/development.md)
- [Versioning and releases](https://github.com/Amir-ESH/TCJ.Framework/blob/main/docs/versioning.md)
- [Release automation](https://github.com/Amir-ESH/TCJ.Framework/blob/main/docs/releasing.md)
- [First preview release notes](https://github.com/Amir-ESH/TCJ.Framework/blob/main/docs/releases/0.1.0-preview.1.md)
- [Release checklist](https://github.com/Amir-ESH/TCJ.Framework/blob/main/RELEASE_CHECKLIST.md)
- [Published-package validation](https://github.com/Amir-ESH/TCJ.Framework/blob/main/docs/published-package-validation.md)
- [Public API compatibility](https://github.com/Amir-ESH/TCJ.Framework/blob/main/docs/api-compatibility.md)
- [Dependency and supply-chain security](https://github.com/Amir-ESH/TCJ.Framework/blob/main/docs/dependency-security.md)
- [Release integrity and build provenance](https://github.com/Amir-ESH/TCJ.Framework/blob/main/docs/release-integrity.md)
- [Reproducible NuGet package builds](https://github.com/Amir-ESH/TCJ.Framework/blob/main/docs/reproducible-builds.md)
- [Software bill of materials](https://github.com/Amir-ESH/TCJ.Framework/blob/main/docs/software-bill-of-materials.md)
- [Code coverage quality gate](https://github.com/Amir-ESH/TCJ.Framework/blob/main/docs/code-coverage.md)
- [Mutation testing quality gate](https://github.com/Amir-ESH/TCJ.Framework/blob/main/docs/mutation-testing.md)
- [Performance benchmarking and regression gate](https://github.com/Amir-ESH/TCJ.Framework/blob/main/docs/performance-benchmarks.md)
- [Architecture tests and module dependency rules](https://github.com/Amir-ESH/TCJ.Framework/blob/main/docs/architecture-tests.md)
- [Contributing](https://github.com/Amir-ESH/TCJ.Framework/blob/main/CONTRIBUTING.md)
- [Security policy](https://github.com/Amir-ESH/TCJ.Framework/blob/main/SECURITY.md)
- [Support](https://github.com/Amir-ESH/TCJ.Framework/blob/main/SUPPORT.md)
- [Changelog](https://github.com/Amir-ESH/TCJ.Framework/blob/main/CHANGELOG.md)

## Contributing

Contributions are welcome through focused issues and pull requests. Read [`CONTRIBUTING.md`](https://github.com/Amir-ESH/TCJ.Framework/blob/main/CONTRIBUTING.md) before opening a change.

## License

TCJ Framework is licensed under the [MIT License](https://github.com/Amir-ESH/TCJ.Framework/blob/main/LICENSE.txt).
