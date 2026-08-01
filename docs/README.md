# TCJ Framework documentation

This directory documents the public behavior of the current `0.1.0-preview.1` source tree. Because the framework is still in preview, treat these pages and the code as a versioned pair.

## Start here

1. [Getting started](getting-started.md)
2. [Architecture and package boundaries](architecture.md)
3. [Product API sample](../samples/TCJ.Empty/README.md)
4. [Development workflow](development.md)
5. [Versioning and releases](versioning.md)

## Package reference

- [`TCJ.Core`](packages/core.md)
- [`TCJ.DependencyInjection`](packages/dependency-injection.md)
- [`TCJ.EntityFrameworkCore`](packages/entity-framework-core.md)
- [`TCJ.EntityFrameworkCore.SqlServer`](packages/entity-framework-core-sqlserver.md)
- [`TCJ.AspNetCore`](packages/aspnet-core.md)

## Guides

- [Result values and HTTP responses](guides/results-and-http.md)
- [Domain events](guides/domain-events.md)
- [Specifications and repositories](guides/specifications-and-repositories.md)
- [Auditing, soft delete, and rowversion](guides/auditing-soft-delete-rowversion.md)
- [Data seeding](guides/data-seeding.md)

## Important preview constraints

- NuGet packages are not published yet.
- Public APIs may change before `1.0.0`.
- Domain-event dispatch is explicit; `SaveChangesAsync` does not dispatch events automatically.
- Soft deletion is explicit through `ISoftDeleteRepository`; calling `Remove` performs physical deletion.
- The sample uses `EnsureCreatedAsync` for local demonstration and is not a migration strategy for production.
