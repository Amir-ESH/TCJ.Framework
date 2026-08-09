# TCJ Framework documentation

This directory documents the public behavior of the current `0.1.0-preview.2` development source tree. Because the framework is still in preview, treat these pages and the code as a versioned pair.

## Start here

1. [Getting started](getting-started.md)
2. [Architecture and package boundaries](architecture.md)
3. [Product API sample](https://github.com/Amir-ESH/TCJ.Framework/blob/develop/samples/TCJ.Empty/README.md)
4. [Development workflow](development.md)
5. [Versioning and releases](versioning.md)
6. [Release automation](releasing.md)
7. [Published-package validation](published-package-validation.md)
8. [Public API compatibility](api-compatibility.md)
9. [Dependency and supply-chain security](dependency-security.md)
10. [Release integrity and build provenance](release-integrity.md)
11. [Software bill of materials](software-bill-of-materials.md)
12. [Reproducible NuGet package builds](reproducible-builds.md)
13. [Code coverage quality gate](code-coverage.md)
14. [Mutation testing quality gate](mutation-testing.md)
15. [Performance benchmarking and regression gate](performance-benchmarks.md)
16. [Architecture tests and module dependency rules](architecture-tests.md)
17. [SQL Server integration testing](sqlserver-integration-testing.md)
18. [ASP.NET Core end-to-end integration testing](aspnetcore-integration-testing.md)
19. [Package consumer compatibility](package-consumer-compatibility.md)
20. [Package upgrade testing](package-upgrade-testing.md)
21. [0.1.0-preview.1 to 0.1.0-preview.2 migration guide](migrations/0.1.0-preview.1-to-0.1.0-preview.2.md)
22. [First preview release notes](release-notes/0.1.0-preview.1.md)

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

- `0.1.0-preview.1` is the latest public preview; the repository currently develops `0.1.0-preview.2`. Pin exact public versions and review the changelog before upgrading.
- Public APIs may change before `1.0.0`.
- Domain-event dispatch is explicit; `SaveChangesAsync` does not dispatch events automatically.
- Soft deletion is explicit through `ISoftDeleteRepository`; calling `Remove` performs physical deletion.
- The sample uses `EnsureCreatedAsync` for local demonstration and is not a migration strategy for production.

## Generated API reference

- [Documentation site home](index.md)
- [Package landing pages](packages/index.md)
- [API reference entry point](api/index.md)
- [Validated consumer examples](examples.md)
- [Documentation authoring and baseline maintenance](documentation-authoring.md)

The generated `artifacts/documentation/api/` and `artifacts/documentation/site/` directories are workflow outputs and must not be committed.

- [Property and fuzz testing](property-and-fuzz-testing.md) explains deterministic FsCheck properties, shrinking/replay, fuzz target corpora, resource limits, failure minimization, and release gating.
