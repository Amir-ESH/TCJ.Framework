# Architecture tests and module dependency rules

TCJ Framework has five runtime packages with intentionally one-way dependencies, plus the analyzer-only `TCJ.Generators` compile-time tooling package. Compiler checks prove that code builds; architecture tests prove that runtime code still belongs in the correct module and that public APIs do not pull infrastructure concerns into lower layers.

The executable policy is stored in `eng/architecture-policy.json` (repository path: `eng/architecture-policy.json`). The test implementation lives in `tests/TCJ.Architecture.Tests` (repository path: `tests/TCJ.Architecture.Tests`).

## Package responsibilities

| Package | Responsibility |
|---|---|
| `TCJ.Core` | Framework-neutral entities, results, guards, domain-event contracts, identifiers, extensions, and security abstractions. |
| `TCJ.DependencyInjection` | Convention-based registration, dependency markers, and domain-event dispatch implementation. |
| `TCJ.EntityFrameworkCore` | Provider-independent EF Core repositories, specifications, unit of work, auditing, soft delete, seeding, and searching. |
| `TCJ.EntityFrameworkCore.SqlServer` | SQL Server provider registration, retry options, and SQL Server model conventions. |
| `TCJ.AspNetCore` | HTTP result mapping, Problem Details, exception handling, current-user resolution, middleware/application integration, and ASP.NET Core options. |

## Approved dependency graph

The policy defines the maximum approved TCJ dependency directions:

```text
TCJ.Core
    ↑
TCJ.DependencyInjection
    ↑
TCJ.EntityFrameworkCore
    ↑
TCJ.EntityFrameworkCore.SqlServer

TCJ.Core
    ↑
TCJ.DependencyInjection
    ↑
TCJ.AspNetCore
```

A project may use a subset of its approved lower-level dependencies. It may not reference a higher-level package. The current direct project references are checked from the production `csproj` files, while compiled assembly references are checked from the built outputs.

The following directions are forbidden:

- `TCJ.Core` to any other TCJ production package;
- `TCJ.DependencyInjection` to EF Core, SQL Server, or ASP.NET Core modules;
- `TCJ.EntityFrameworkCore` to SQL Server or ASP.NET Core modules;
- `TCJ.EntityFrameworkCore.SqlServer` to `TCJ.AspNetCore`;
- `TCJ.AspNetCore` to SQL Server-specific modules;
- any dependency cycle between production modules.

Provider-independent projects also reject SQL Server package references. Core and dependency-injection modules reject EF Core and ASP.NET Core infrastructure references.

## Namespace ownership

Every source-declared type must use the namespace root of its owning assembly:

```text
TCJ.Core.*
TCJ.DependencyInjection.*
TCJ.EntityFrameworkCore.*
TCJ.EntityFrameworkCore.SqlServer.*
TCJ.AspNetCore.*
```

A type compiled into one package cannot be declared under another package root. Compiler-generated implementation types, including generated regular-expression runners and collection-expression helpers, are excluded because they do not represent source-owned package namespaces. Types under an `Internal` namespace cannot be public. Test-only namespaces and test-fixture naming are rejected in production assemblies.

## Public API boundaries

Exported types are inspected through their base types, implemented interfaces, constructors, methods, properties, fields, events, generic arguments, and generic constraints.

The tests reject public contracts that expose infrastructure forbidden for their package. Examples include:

- EF Core, ASP.NET Core, or SQL client types leaking from `TCJ.Core`;
- EF Core or ASP.NET Core types leaking from `TCJ.DependencyInjection`;
- SQL Server or ASP.NET Core types leaking from provider-independent EF Core contracts;
- EF Core or SQL Server types leaking from `TCJ.AspNetCore`;
- public interfaces exposing concrete TCJ implementation classes.

This complements package compatibility validation: API compatibility detects accidental binary breaks, while architecture tests detect an API that is technically compatible but placed in the wrong layer.

## Naming and visibility rules

The initial suite enforces only patterns already established by the repository:

- containers with extension methods are static and end with `Extensions`; established fluent guard containers such as `TCJ.Core.Guards.Check` are explicit policy exceptions in `approvedExtensionContainers`;
- public option types are explicitly listed in `approvedPublicOptionTypes`; `TCJ.Core.Diagnostics.TcjTelemetryOptions` is approved because the observability contract adds the cross-package, backend-neutral observability configuration contract;
- repository interfaces use the `I` prefix;
- SQL Server-specific types remain in `TCJ.EntityFrameworkCore.SqlServer`;
- ASP.NET Core middleware and exception-handler types remain in `TCJ.AspNetCore`;
- implementation helpers under `Internal` namespaces are not public.

These rules are intentionally narrow. A naming preference should not become an architecture rule unless it is stable, useful, and already established across the codebase.

## Run locally

Validate the policy and repository integration:

```bash
python3 eng/verify-architecture-policy.py validate-config
```

Run every test through the solution:

```bash
dotnet test TCJ.slnx -c Release
```

Run only architecture tests:

```bash
dotnet test tests/TCJ.Architecture.Tests/TCJ.Architecture.Tests.csproj \
  -c Release \
  -- --filter-trait "Category=Architecture"
```

Generate the readable policy summary used by GitHub Actions:

```bash
python3 eng/verify-architecture-policy.py write-summary \
  --output artifacts/architecture/ARCHITECTURE_TEST_SUMMARY.md
```

Architecture tests run in normal CI, release preflight, and the official tagged release workflow as part of `dotnet test TCJ.slnx`.

## Add a new module

A new production module requires one coordinated change:

1. Add the project and package ID to the release manifest and solution.
2. Add its project path, namespace root, allowed dependencies, forbidden dependency prefixes, and public API restrictions to `eng/architecture-policy.json`.
3. Reference the project from `TCJ.Architecture.Tests` for inspection.
4. Update the dependency diagram and package responsibility documentation.
5. Add focused validation scenarios for its boundaries.
6. Run policy validation, architecture tests, package validation, and the full solution tests.

The policy verifier intentionally rejects an unknown or missing production assembly so a package cannot silently escape coverage.

## Propose an intentional architecture change

An intentional dependency-direction change must be visible and reviewed. The pull request should:

- explain the use case and why the existing boundary is insufficient;
- update `eng/architecture-policy.json`;
- update the approved dependency graph in this document and [`architecture.md`](architecture.md);
- describe public API and package compatibility effects;
- include tests proving the new boundary and preserving acyclicity.

Suppressing a failing architecture test, weakening a forbidden prefix, or adding a broad exception only to make CI green is discouraged. Architecture exceptions tend to become permanent coupling and should instead be represented as an explicit, documented policy decision.

## Failure messages

Failures identify:

- the assembly, project, or type that violated the rule;
- the dependency, namespace, or public API type that was found;
- the expected allowed direction or prefix rule;
- the policy and documentation paths;
- the process required for an intentional change.

This keeps failures actionable instead of returning only a generic assertion mismatch.
