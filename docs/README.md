# TCJ Framework documentation

These documents describe the public TCJ Framework contract and the repository's engineering guarantees. The latest published preview is `0.1.0-preview.3`; active development targets `0.1.0-preview.4`. Consumer installation examples stay pinned to the latest verified NuGet.org release unless a page is explicitly about development or migration work.

## Start here

- [Getting started](getting-started.md)
- [Package reference](packages/index.md)
- [Architecture and package boundaries](architecture.md)
- [Validated examples](examples.md)
- [Development workflow](development.md)
- [Versioning and releases](versioning.md)

## Product guides

- [Result values and HTTP responses](guides/results-and-http.md)
- [Domain events](guides/domain-events.md)
- [Specifications and repositories](guides/specifications-and-repositories.md)
- [Auditing, soft delete, and rowversion](guides/auditing-soft-delete-rowversion.md)
- [Data seeding](guides/data-seeding.md)
- [Native AOT and trimming](guides/native-aot-and-trimming.md)
- [Resilience](resilience.md)
- [Health checks and startup diagnostics](health-checks.md)
- [Transactional outbox](outbox.md)
- [Observability](observability.md)
- [Strong typed IDs and Value Objects](guides/strong-types.md)

## Compatibility and quality

- [Public API compatibility](api-compatibility.md)
- [Package consumer compatibility](package-consumer-compatibility.md)
- [Package upgrade testing](package-upgrade-testing.md)
- [SQL Server integration testing](sqlserver-integration-testing.md)
- [ASP.NET Core integration testing](aspnetcore-integration-testing.md)
- [Architecture tests](architecture-tests.md)
- [Analyzer diagnostic governance](analyzers/README.md)
- [Code coverage](code-coverage.md)
- [Mutation testing](mutation-testing.md)
- [Property and fuzz testing](property-and-fuzz-testing.md)
- [Concurrency stress testing](concurrency-stress-testing.md)
- [Performance benchmarks](performance-benchmarks.md)

## Release and supply chain

- [Release automation](releasing.md)
- [Published-package validation](published-package-validation.md)
- [Dependency and supply-chain security](dependency-security.md)
- [Release integrity and build provenance](release-integrity.md)
- [Software bill of materials](software-bill-of-materials.md)
- [Reproducible builds](reproducible-builds.md)
- [Documentation authoring](documentation-authoring.md)

## Package reference

- [`TCJ.Core`](packages/tcj-core.md)
- [`TCJ.DependencyInjection`](packages/tcj-dependencyinjection.md)
- [`TCJ.EntityFrameworkCore`](packages/tcj-entityframeworkcore.md)
- [`TCJ.EntityFrameworkCore.SqlServer`](packages/tcj-entityframeworkcore-sqlserver.md)
- [`TCJ.AspNetCore`](packages/tcj-aspnetcore.md)

## Migrations and release notes

- [0.1.0-preview.3 to 0.1.0-preview.4 migration guide](migrations/0.1.0-preview.3-to-0.1.0-preview.4.md)
- [0.1.0-preview.2 to 0.1.0-preview.3 migration guide](migrations/0.1.0-preview.2-to-0.1.0-preview.3.md)
- [0.1.0-preview.1 to 0.1.0-preview.2 migration guide](migrations/0.1.0-preview.1-to-0.1.0-preview.2.md)
- [0.1.0-preview.3 release notes](release-notes/0.1.0-preview.3.md)
- [0.1.0-preview.2 release notes](release-notes/0.1.0-preview.2.md)
- [0.1.0-preview.1 release notes](release-notes/0.1.0-preview.1.md)

## Important preview constraints

- `0.1.0-preview.3` is the latest public preview; `0.1.0-preview.4` is under active development.
- Public APIs may change before `1.0.0`; pin exact preview versions in production-like environments.
- Domain-event dispatch is explicit by default. When the transactional outbox is enabled, `SaveChanges` persists pending events transactionally and a separate processor dispatches committed messages later.
- Soft deletion is explicit through `ISoftDeleteRepository`; `Remove` performs physical deletion.
- The sample uses `EnsureCreatedAsync` for local demonstration and is not a production migration strategy.

## Generated API reference

- [Documentation site home](index.md)
- [API reference entry point](api/index.md)
- [Validated consumer examples](examples.md)

The generated `artifacts/documentation/api/` and `artifacts/documentation/site/` directories are workflow outputs and must not be committed.
