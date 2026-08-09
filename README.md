# TCJ Framework

> **Latest published preview:** `0.1.0-preview.1`
> **Current development version:** `0.1.0-preview.2`

[![CI](https://github.com/Amir-ESH/TCJ.Framework/actions/workflows/ci.yml/badge.svg?branch=develop)](https://github.com/Amir-ESH/TCJ.Framework/actions/workflows/ci.yml)
[![Package consumer compatibility](https://github.com/Amir-ESH/TCJ.Framework/actions/workflows/consumer-compatibility.yml/badge.svg?branch=develop)](https://github.com/Amir-ESH/TCJ.Framework/actions/workflows/consumer-compatibility.yml)

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

## API reference and documentation quality

The repository builds a DocFX site from the five production projects and the conceptual documentation under [`docs/`](docs/README.md). The documentation gate measures public API coverage, rejects new undocumented APIs, validates XML references and internal links, and compiles selected consumer examples.

Start with the [documentation home](docs/index.md), browse the [package landing pages](docs/packages/index.md), or read the [documentation authoring guide](docs/documentation-authoring.md).

Local validation:

```bash
dotnet tool restore
dotnet build TCJ.slnx --configuration Release
dotnet docfx metadata docfx/docfx.json --warningsAsErrors
dotnet docfx build docfx/docfx.json --warningsAsErrors
python3 eng/verify-documentation.py verify \
  --configuration Release \
  --build-root src \
  --api-root artifacts/documentation/api
```

## Requirements

- .NET SDK `10.0.100` or a compatible SDK selected by [`global.json`](https://github.com/Amir-ESH/TCJ.Framework/blob/main/global.json)
- SQL Server only when using `TCJ.EntityFrameworkCore.SqlServer` or running the sample application
- Docker with Linux-container support when running the real SQL Server integration suite
- No external web server is required for the ASP.NET Core end-to-end integration suite; it uses an in-memory TestServer on Linux and Windows

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
dotnet test TCJ.slnx -c Release --no-build --filter "Category!=SqlServer&Category!=AspNetCore&Category!=Concurrency"
```

Run the Product API sample:

```bash
dotnet run --project samples/TCJ.Empty/TCJ.Empty.csproj
```

The default Development connection string uses SQL Server LocalDB on Windows. See the [sample README](https://github.com/Amir-ESH/TCJ.Framework/blob/main/samples/TCJ.Empty/README.md) for configuration details.

Provider-specific integration tests start a disposable pinned SQL Server container and require Docker; they do not use LocalDB or permanent database secrets. See [SQL Server integration testing](docs/sqlserver-integration-testing.md) for local commands, isolation, diagnostics, and CI behavior.

ASP.NET Core end-to-end tests run a real in-memory application through TestServer, deterministic test authentication, request scopes, exception handling, Problem Details, and cancellation on Linux and Windows. See [ASP.NET Core integration testing](docs/aspnetcore-integration-testing.md).

TCJ also exposes backend-neutral `ActivitySource` and `Meter` contracts for domain events, dependency registration, repositories, Unit of Work/transactions, SQL Server setup, and ASP.NET Core exception handling. Production packages do not depend on an exporter. See [Diagnostics and OpenTelemetry observability](docs/observability.md).

Package-consumer compatibility is also tested from outside the production solution. Six clean applications restore only TCJ NuGet packages, verify the exact package version and source, build and run on Linux/Windows/macOS, exercise ASP.NET Core and EF Core wiring, and validate `.nupkg`, `.snupkg`, portable PDB, XML documentation, and Source Link metadata. See [Package consumer compatibility](docs/package-consumer-compatibility.md).

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
  --filter "Category!=SqlServer&Category!=AspNetCore&Category!=Concurrency" \
  --collect:"XPlat Code Coverage" \
  --settings tests/coverlet.runsettings \
  --results-directory TestResults
python3 eng/verify-coverage.py verify
python3 eng/verify-mutation-results.py validate-config
python3 eng/verify-performance-results.py validate-config
python3 eng/verify-architecture-policy.py validate-config
python3 eng/verify-sbom.py validate-config
python3 eng/verify-reproducible-build.py validate-config
python3 eng/verify-sqlserver-integration.py validate-config
python3 eng/verify-aspnetcore-integration.py validate-config
python3 eng/verify-observability.py validate-config
dotnet pack TCJ.slnx -c Release --no-build
```

NuGet packages and symbol packages are written to `artifacts/packages`. Restore audits direct and transitive dependencies, and Pack validates binary compatibility against the latest published TCJ packages. CI enforces line and branch coverage, while the dedicated **Mutation testing** workflow validates test effectiveness for the controlled `TCJ.Core` and `TCJ.DependencyInjection` scope. The dedicated **Performance benchmarks** workflow uses BenchmarkDotNet to record runtime and managed-allocation data and enforces only within-run ratios, avoiding unreliable absolute-time comparisons between different hosted runners. The solution also contains deterministic architecture tests that enforce the approved package graph, namespace ownership, and public API infrastructure boundaries. The first valid mutation run creates a candidate that must be reviewed and accepted before the recorded baseline can pass normal verification. CI also generates and validates a CycloneDX JSON software bill of materials, includes it in `artifacts/release/SHA256SUMS`, and uploads the SBOM summary. The dedicated **Reproducible builds** workflow creates two isolated package builds and compares assemblies, portable PDBs, Source Link, XML documentation, NuSpec metadata, optional physical source entries, and every extracted package payload. Release preflight and official tagged releases promote only a verified package set before SBOM generation, checksums, attestation, NuGet publication, and GitHub Release creation.

## Documentation map

- [Getting started](https://github.com/Amir-ESH/TCJ.Framework/blob/main/docs/getting-started.md)
- [Architecture and package boundaries](https://github.com/Amir-ESH/TCJ.Framework/blob/main/docs/architecture.md)
- [Package reference](https://github.com/Amir-ESH/TCJ.Framework/blob/main/docs/README.md#package-reference)
- [Guides and recipes](https://github.com/Amir-ESH/TCJ.Framework/blob/main/docs/README.md#guides)
- [Development workflow](https://github.com/Amir-ESH/TCJ.Framework/blob/main/docs/development.md)
- [Versioning and releases](https://github.com/Amir-ESH/TCJ.Framework/blob/main/docs/versioning.md)
- [Release automation](https://github.com/Amir-ESH/TCJ.Framework/blob/main/docs/releasing.md)
- [First preview release notes](https://github.com/Amir-ESH/TCJ.Framework/blob/main/docs/release-notes/0.1.0-preview.1.md)
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
- [ASP.NET Core end-to-end integration testing](https://github.com/Amir-ESH/TCJ.Framework/blob/main/docs/aspnetcore-integration-testing.md)
- [Diagnostics and OpenTelemetry observability](https://github.com/Amir-ESH/TCJ.Framework/blob/main/docs/observability.md)
- [Package consumer compatibility](https://github.com/Amir-ESH/TCJ.Framework/blob/main/docs/package-consumer-compatibility.md)
- [Package upgrade testing](https://github.com/Amir-ESH/TCJ.Framework/blob/main/docs/package-upgrade-testing.md)
- [0.1.0-preview.1 to 0.1.0-preview.2 migration guide](https://github.com/Amir-ESH/TCJ.Framework/blob/main/docs/migrations/0.1.0-preview.1-to-0.1.0-preview.2.md)
- [Contributing](https://github.com/Amir-ESH/TCJ.Framework/blob/main/CONTRIBUTING.md)
- [Security policy](https://github.com/Amir-ESH/TCJ.Framework/blob/main/SECURITY.md)
- [Support](https://github.com/Amir-ESH/TCJ.Framework/blob/main/SUPPORT.md)
- [Changelog](https://github.com/Amir-ESH/TCJ.Framework/blob/main/CHANGELOG.md)


## Contributing

Contributions are welcome through focused issues and pull requests. Read [`CONTRIBUTING.md`](https://github.com/Amir-ESH/TCJ.Framework/blob/main/CONTRIBUTING.md) before opening a change.

## License

TCJ Framework is licensed under the [MIT License](https://github.com/Amir-ESH/TCJ.Framework/blob/main/LICENSE.txt).

## Property-based and fuzz testing

Foundational `TCJ.Core` and `TCJ.DependencyInjection` behavior is protected by deterministic FsCheck properties and five bounded fuzz targets. Pull requests get reproducible generated-input coverage and short fuzz campaigns; scheduled and release workflows run longer or release-blocking validation. See [Property and fuzz testing](docs/property-and-fuzz-testing.md).


## Concurrency stress testing

TCJ documents and continuously verifies concurrency boundaries rather than assuming every abstraction is thread-safe. A deterministic stress suite exercises dependency registration, service lifetimes, domain-event dispatch, ASP.NET Core request/current-user isolation, independent EF Core scopes, cancellation/disposal races, and real SQL Server transactions. Pull requests run bounded workloads, while scheduled and release workflows run stronger replayable campaigns. See [Concurrency stress testing](docs/concurrency-stress-testing.md).
