# Architecture and package boundaries

TCJ Framework is split into small packages with one-way dependencies. Applications can adopt only the layers they need.

```text
TCJ.Core
├── TCJ.DependencyInjection
├── TCJ.EntityFrameworkCore
│   └── TCJ.EntityFrameworkCore.SqlServer
└── TCJ.AspNetCore
```

`TCJ.EntityFrameworkCore.SqlServer` depends on both `TCJ.Core` and `TCJ.EntityFrameworkCore`. `TCJ.DependencyInjection` and `TCJ.AspNetCore` depend on `TCJ.Core`. `TCJ.Core` does not reference ASP.NET Core or Entity Framework Core.

## Design principles

### Framework-neutral domain primitives

Entities, Result values, domain events, identifiers, and the current-user abstraction live in `TCJ.Core`. This keeps the domain layer independent from transport and persistence technology.

### Explicit persistence

Repository write operations stage changes; `IUnitOfWork.SaveChangesAsync` persists them. Soft deletion has a separate repository contract so physical deletion and logical deletion cannot be confused accidentally.

### Explicit query intent

Read repositories return no-tracking queries by default. Tracking must be requested through `TrackedQuery` or a specification configured with `AsTracking`.

### Explicit assembly scanning

Dependency registration scans only assemblies supplied to `AddTcjDependencyInjection`. The framework does not scan every loaded assembly implicitly.

### Explicit domain-event dispatch

Entities can collect pending domain events and `IDomainEventDispatcher` invokes registered handlers sequentially. Persistence does not automatically publish or clear domain events in the current preview.

### Host-owned configuration

The host application owns connection strings, authentication, authorization, migrations, logging, and deployment policy. TCJ supplies integrations without hiding the underlying .NET abstractions.

## Typical request flow

```text
HTTP endpoint
  → application service
    → Result<T>
    → repository/specification
    → unit of work
    → auditing interceptor
    → SQL Server
  → Result-to-HTTP mapping
  → Problem Details or success response
```

## Lifetime model

- Framework defaults such as `IGuidGenerator` and `TimeProvider` are singletons.
- `IDomainEventDispatcher`, EF repositories, `IUnitOfWork`, current-user resolution, and EF interceptors are scoped.
- Domain-event handlers are transient.
- Application services use explicit TCJ lifetime marker interfaces.

## What the framework does not currently provide

- Automatic domain-event dispatch from `SaveChangesAsync`
- An outbox implementation
- Authentication or authorization setup
- Database migrations for consumer applications
- Provider packages other than SQL Server
- A distributed transaction abstraction

Repository restore is restricted to the configured NuGet.org source and audits direct and transitive dependencies. Packable TCJ projects use SDK package validation to detect accidental binary-breaking API changes against the latest published baseline. The remaining boundaries are intentional for the preview and should be considered when designing applications on top of TCJ.

## Executable architecture policy

The approved package graph, namespace roots, forbidden infrastructure prefixes, and public option allowlist are versioned in [`eng/architecture-policy.json`](https://github.com/Amir-ESH/TCJ.Framework/blob/develop/eng/architecture-policy.json). The `TCJ.Architecture.Tests` project checks both production project references and compiled assembly metadata, detects cycles, validates namespace ownership, and rejects infrastructure leakage through public APIs.

Run the policy validator and focused test category with:

```bash
python3 eng/verify-architecture-policy.py validate-config
dotnet test tests/TCJ.Architecture.Tests/TCJ.Architecture.Tests.csproj \
  -c Release \
  -- --filter-trait "Category=Architecture"
```

See [Architecture tests and module dependency rules](architecture-tests.md) for the complete dependency graph and change process.
