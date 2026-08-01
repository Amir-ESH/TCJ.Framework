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
- Automatic public API compatibility validation

These boundaries are intentional for the preview and should be considered when designing applications on top of TCJ.
