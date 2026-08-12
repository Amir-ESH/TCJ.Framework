<p align="center">
  <img src=".github/assets/tcj-framework-banner.png" alt="TCJ Framework — Modular, Reliable, Extensible" width="100%" />
</p>

<p align="center">
  <a href="https://github.com/Amir-ESH/TCJ.Framework/actions/workflows/ci.yml"><img alt="CI" src="https://img.shields.io/github/actions/workflow/status/Amir-ESH/TCJ.Framework/ci.yml?branch=develop&style=flat-square&label=CI"></a>
  <a href="https://www.nuget.org/packages/TCJ.Core"><img alt="NuGet" src="https://img.shields.io/nuget/v/TCJ.Core.svg?style=flat-square&label=NuGet"></a>
  <a href="https://www.nuget.org/packages/TCJ.Core"><img alt="NuGet downloads" src="https://img.shields.io/nuget/dt/TCJ.Core.svg?style=flat-square&label=downloads"></a>
  <img alt=".NET 10" src="https://img.shields.io/badge/.NET-10.0-512BD4?style=flat-square">
  <a href="LICENSE.txt"><img alt="License: LGPL-3.0-only" src="https://img.shields.io/badge/license-LGPL--3.0--only-blue.svg?style=flat-square"></a>
</p>

<p align="center">
  <strong>A modular, production-minded foundation for modern .NET applications.</strong><br />
  Domain building blocks, explicit dependency injection, EF Core infrastructure, SQL Server integration, ASP.NET Core primitives, Native AOT evidence, and release-grade engineering gates.
</p>

> **Latest published preview:** `0.1.0-preview.1`  
> **Current development version:** `0.1.0-preview.2`
>
> TCJ Framework is still pre-`1.0`. Public APIs may change between preview releases; pin exact preview versions in production-like environments.

## Why TCJ Framework?

TCJ is designed to provide reusable infrastructure without hiding architectural boundaries or forcing an application into a single monolithic framework model.

| Area | What TCJ provides |
| --- | --- |
| Domain foundation | Entities, Result pattern, structured errors, guards, UUID v7 identifiers, domain-event contracts, resilience primitives, health contracts, and transactional-outbox building blocks. |
| Dependency injection | Convention-based registration plus explicit reflection-free registration for Native AOT-sensitive paths and sequential domain-event dispatch. |
| Persistence | Repository/specification abstractions, Unit of Work, auditing, soft delete, ordered/idempotent seeding, entity metadata, and EF Core integration. |
| SQL Server | Provider registration, rowversion conventions, bounded retry integration, readiness checks, and transactional outbox persistence/processing. |
| ASP.NET Core | Current-user access, Result-to-HTTP mapping, Problem Details, centralized exception handling, health endpoints, and Minimal API integration. |
| Engineering quality | Binary API validation, dependency audit, coverage, mutation tests, property/fuzz tests, concurrency stress, architecture tests, reproducible builds, SBOMs, package-consumer tests, and release provenance. |

## Packages

Install only the modules your application needs.

| Package | Purpose | Native AOT status |
| --- | --- | --- |
| [`TCJ.Core`](https://www.nuget.org/packages/TCJ.Core) | Domain primitives, Result, errors, guards, identifiers, diagnostics, resilience, health and outbox contracts. | **Full** |
| [`TCJ.DependencyInjection`](https://www.nuget.org/packages/TCJ.DependencyInjection) | Service registration and domain-event dispatch. | **Full** on the explicit AOT-safe path |
| [`TCJ.EntityFrameworkCore`](https://www.nuget.org/packages/TCJ.EntityFrameworkCore) | Repositories, specifications, Unit of Work, auditing, soft delete, seeding, and EF infrastructure. | **Experimental** |
| [`TCJ.EntityFrameworkCore.SqlServer`](https://www.nuget.org/packages/TCJ.EntityFrameworkCore.SqlServer) | SQL Server provider integration and conventions. | **Experimental** |
| [`TCJ.AspNetCore`](https://www.nuget.org/packages/TCJ.AspNetCore) | ASP.NET Core application primitives and Minimal API integration. | **Full** for the verified Minimal API path |

The stable packaged Native AOT release guarantee covers `TCJ.Core`, the explicit AOT-safe `TCJ.DependencyInjection` path, and the supported `TCJ.AspNetCore` Minimal API path on `linux-x64`. EF Core Native AOT remains explicitly experimental because upstream EF Native AOT/query precompilation still has limitations. See [Native AOT and trimming](docs/guides/native-aot-and-trimming.md).

## Install

Published preview packages:

```bash
dotnet add package TCJ.Core --version 0.1.0-preview.1
dotnet add package TCJ.DependencyInjection --version 0.1.0-preview.1
dotnet add package TCJ.EntityFrameworkCore --version 0.1.0-preview.1
dotnet add package TCJ.EntityFrameworkCore.SqlServer --version 0.1.0-preview.1
dotnet add package TCJ.AspNetCore --version 0.1.0-preview.1
```

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

An EF Core context can opt into TCJ persistence contracts and SQL Server conventions:

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

For a complete runnable example, see [`samples/TCJ.Empty`](samples/TCJ.Empty/README.md).

## Architecture

TCJ keeps dependencies directional so consumers can stop at the layer they actually need:

```text
                         ┌─────────────────────┐
                         │   TCJ.AspNetCore    │
                         └──────────┬──────────┘
                                    │
                         ┌──────────▼──────────┐
                         │ TCJ.DependencyInjection │
                         └──────────┬──────────┘
                                    │
┌──────────────────────────────┐    │    ┌─────────────────────────────┐
│ TCJ.EntityFrameworkCore      │────┼────│ TCJ.EntityFrameworkCore    │
│          .SqlServer          │    │    │                             │
└──────────────┬───────────────┘    │    └──────────────┬──────────────┘
               │                    │                   │
               └────────────────────▼───────────────────┘
                              ┌──────────┐
                              │ TCJ.Core │
                              └──────────┘
```

The executable architecture-test suite validates the approved package graph, detects cycles, enforces namespace ownership, and prevents infrastructure types from leaking into lower-level public APIs. See [Architecture and package boundaries](docs/architecture.md) and [Architecture tests](docs/architecture-tests.md).

## Native AOT

TCJ does not treat "AOT compatible" as a documentation-only claim. Blocking CI and release workflows pack the candidate NuGet packages, restore a clean package-only consumer, publish it as a self-contained `linux-x64` Native AOT executable, run the native binary, and verify that the exact candidate package versions were loaded. Unexpected trimming/AOT diagnostics fail the gate.

The verified stable scope is intentionally narrower than the whole repository. `TCJ.EntityFrameworkCore` and `TCJ.EntityFrameworkCore.SqlServer` remain **Experimental** for Native AOT and are not promoted into the stable release guarantee.

Read the full contract in [Native AOT and trimming](docs/guides/native-aot-and-trimming.md).

## Quality and release guarantees

TCJ's release pipeline is designed around evidence produced from the same candidate package set that is eventually published.

- **API compatibility:** SDK package validation compares public binary surface against the latest published immutable baseline.
- **Package consumers:** clean applications restore TCJ packages rather than repository project references and run across Linux, Windows, and macOS.
- **Upgrade compatibility:** baseline-to-candidate consumers exercise direct package upgrades and normalized runtime behavior.
- **Tests:** unit/integration suites are reinforced with coverage, mutation testing, property tests, bounded fuzzing, and deterministic concurrency stress.
- **Architecture:** executable rules enforce module direction, cycles, namespace ownership, and public API boundaries.
- **Database integration:** real SQL Server Testcontainers validate migrations, transactions, rowversion concurrency, outbox behavior, health checks, and resilience boundaries.
- **Observability:** stable Activity/Meter contracts are verified without forcing an exporter dependency on consumers.
- **Reproducibility:** isolated builds compare assemblies, portable PDBs, Source Link, NuSpec metadata, and package payloads.
- **Supply chain:** NuGet Audit, dependency review, CycloneDX SBOMs, SHA-256 manifests, and GitHub build-provenance attestations protect releases.
- **Documentation:** DocFX metadata/site builds, public API documentation coverage, links, and selected consumer examples are validated as release gates.

See [Release automation](docs/releasing.md) and the [Release checklist](RELEASE_CHECKLIST.md) for the complete gate set.

## Build from source

Requirements:

- the .NET SDK selected by [`global.json`](global.json);
- Docker with Linux-container support for real SQL Server integration suites;
- SQL Server only when directly running SQL Server-dependent samples outside the containerized tests.

```bash
git clone https://github.com/Amir-ESH/TCJ.Framework.git
cd TCJ.Framework

dotnet restore TCJ.slnx
dotnet build TCJ.slnx -c Release --no-restore
dotnet test TCJ.slnx -c Release --no-build \
  --filter "Category!=SqlServer&Category!=AspNetCore&Category!=Concurrency"
```

Run the sample:

```bash
dotnet run --project samples/TCJ.Empty/TCJ.Empty.csproj
```

## Documentation

Start here:

- [Documentation home](docs/index.md)
- [Getting started](docs/getting-started.md)
- [Package reference](docs/packages/index.md)
- [Architecture](docs/architecture.md)
- [Development workflow](docs/development.md)
- [Native AOT and trimming](docs/guides/native-aot-and-trimming.md)
- [SQL Server integration testing](docs/sqlserver-integration-testing.md)
- [ASP.NET Core integration testing](docs/aspnetcore-integration-testing.md)
- [Package consumer compatibility](docs/package-consumer-compatibility.md)
- [Package upgrade testing](docs/package-upgrade-testing.md)
- [Observability](docs/observability.md)
- [Resilience](docs/resilience.md)
- [Health checks](docs/health-checks.md)
- [Transactional outbox](docs/outbox.md)
- [Security](SECURITY.md)
- [Project governance](GOVERNANCE.md)
- [Contributor License Agreement](CLA.md)
- [Trademark and brand policy](TRADEMARKS.md)
- [Support](SUPPORT.md)
- [Changelog](CHANGELOG.md)

## Contributing

Focused issues and pull requests are welcome. Please read [`CONTRIBUTING.md`](CONTRIBUTING.md) before starting a change. Public API and architecture changes should be discussed before implementation, and new behavior is expected to arrive with focused validation rather than by weakening an existing gate.

Contributions are accepted under the [TCJ Contributor License Agreement](CLA.md). Contributors retain copyright in their original Contributions while granting the Official TCJ Project the rights needed to maintain, distribute, sublicense, and relicense accepted work. See [Project Governance](GOVERNANCE.md) for owner-reserved decisions and upstream authority.

## License and brand

The TCJ Framework code in this development line is licensed under the **GNU Lesser General Public License v3.0 only (`LGPL-3.0-only`)**. See [`LICENSE.txt`](LICENSE.txt).

The LGPL is intended for libraries and permits use in applications licensed under different terms, including proprietary applications, subject to the LGPL's conditions. If you distribute a Combined Work using a static-link-like model—including Native AOT—review the LGPL's Combined Work and relinking/recombination requirements for your distribution model.

The software license and the project brand are separate. The LGPL does not grant a general right to present an unofficial fork, modified package, hosted service, or other product as an official TCJ release. Truthful references such as **"built with TCJ Framework"** are welcome; use of the TCJ name, logo, banner, or official `TCJ.*` package identity must avoid confusion about origin or endorsement. See [`TRADEMARKS.md`](TRADEMARKS.md).

> **License history:** copies and releases previously distributed under the MIT License keep the permissions already granted with those copies. The current development line is licensed under LGPL-3.0-only for new distributions that identify this license.

Independent forks may be developed and distributed commercially under the applicable LGPL terms. Forking or contributing does not grant ownership, merge/release authority, or governance control over the **Official TCJ Project**. Those responsibilities are defined in [`GOVERNANCE.md`](GOVERNANCE.md).

---

<p align="center">
  <strong>TCJ Framework</strong> — Modular · Reliable · Extensible
</p>
