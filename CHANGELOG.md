# Changelog

All notable changes to TCJ Framework will be documented in this file.

The project follows semantic versioning. Until `1.0.0`, preview releases may include breaking public API changes.

## [Unreleased]

## [0.1.0-preview.2] - 2026-08-15

### Added

- Blocking packaged Native AOT release evidence for `TCJ.Core`, the explicit AOT-safe `TCJ.DependencyInjection` path, and the supported `TCJ.AspNetCore` Minimal API path, including exact local NuGet/version verification, `linux-x64` native publish/execute coverage, zero-warning enforcement, retained release evidence, and publish-time re-verification.
- First-class TCJ liveness/readiness health checks, startup diagnostics, bounded SQL Server connectivity and migration readiness, safe ASP.NET Core health endpoints, telemetry, CI/release enforcement, and published-package smoke coverage.
- Deterministic concurrency and thread-safety stress testing with replayable seeds, deadlock/timeout diagnostics, request/DI/domain-event/EF/SQL Server scenarios, policy verification, scheduled automation, and release-blocking gates.
- An opt-in SQL Server transactional outbox with same-transaction domain-event persistence, stable message IDs, safe concurrent lease claiming, bounded retry/dead-letter processing, explicit replay, retention cleanup, idempotency metadata, OpenTelemetry diagnostics, health checks, SQL Server Testcontainers coverage, policy verification, and release gates.
- Explicit bounded retry, cooperative timeout, isolated circuit-breaker, opt-in domain-event handler retry, and transaction-safe SQL Server execution-strategy primitives, with deterministic fault injection, resilience telemetry/contracts, benchmarks, CI/release gates, and idempotency guidance.
- Backend-neutral tracing and metrics across domain-event dispatch, dependency registration, EF Core repositories and Unit of Work/transactions, SQL Server configuration, and ASP.NET Core exception handling, with stable versioned contracts, sensitive-data-safe defaults, dedicated observability tests/verifier, CI/release enforcement, benchmarks, and OpenTelemetry sample configuration.
- Deterministic property-based testing and bounded fuzzing for foundational Core and dependency-registration APIs, including custom generators, shrinking/replay, reviewed seed corpora, failure minimization, policy verification, dedicated/scheduled automation, and release-blocking quality gates.
- Automated package upgrade compatibility from the published baseline to the release candidate, including six clean-room scenarios, source-tree stability, dependency/behavior diffs, persisted-data validation, migration guidance, release gates, and post-publication revalidation.
- Clean-room NuGet package consumer compatibility across Linux, Windows, and macOS, with six package-only applications, isolated restore/source verification, package/symbol/Source Link validation, release gates, and published-package consumer reuse.
- Cross-platform ASP.NET Core end-to-end integration coverage using an in-memory TestServer, deterministic authentication, request-scope/current-user isolation, Problem Details and exception behavior, cancellation, sanitized diagnostics, policy verification, and release gating.
- Real SQL Server integration coverage through Testcontainers, including isolated migrated databases, repository/transaction/auditing/storage/concurrency scenarios, sanitized diagnostics, and policy verification.
- Dedicated SQL Server integration CI plus release-preflight and tagged-release enforcement for relevant database changes.
- Automated DocFX API reference generation and a policy-backed documentation quality gate for all five public packages, including measured coverage, explicit baseline debt, validated examples, link checks, and CI/release artifacts.
- Reproducible NuGet package verification for all five primary and symbol packages using two isolated builds, semantic content comparison, focused difference reports, and a dedicated scheduled/manual/pull-request workflow.
- Release-preflight and tagged-release enforcement that promotes only a verified package set before SBOM generation, checksums, attestations, and publication.
- CycloneDX JSON SBOM generation and strict verification for all five release packages, restored direct/transitive NuGet dependencies, licenses, hashes, repository metadata, and source provenance.
- CI, release-preflight, and tagged-release integration that includes the SBOM in checksums, workflow artifacts, attestations, and GitHub Release assets.
- SDK package validation against the latest published TCJ package baseline.
- CI enforcement for accidental binary-breaking public API changes.
- Public API compatibility policy and maintainer workflow.
- Automated verification of listed NuGet packages using the NuGet V3 registration and flat-container APIs.
- Cross-platform smoke consumer that restores, builds, and runs all published TCJ packages from NuGet.org.
- Weekly and manually triggered published-package smoke-test workflow.
- Repository-wide NuGet Audit policy for direct and transitive dependencies.
- Pull-request dependency review and scheduled dependency-audit workflows.
- Explicit NuGet.org package-source mapping and audit-source configuration.
- SHA-256 manifests for complete primary and symbol package release sets.
- Cryptographically signed GitHub build-provenance attestations for official release assets.
- Release-integrity verification before artifact upload, NuGet publication, and GitHub Release creation.
- Cross-package Cobertura coverage collection with enforced line and branch minimums.
- Merged coverage summaries and raw-report artifacts for CI, preflight, and tagged releases.
- Stryker.NET mutation testing for a controlled `TCJ.Core` and `TCJ.DependencyInjection` baseline, with HTML/JSON reports and a reviewed baseline-candidate workflow.
- BenchmarkDotNet performance baselines for foundational Core and dependency-registration operations, with allocation diagnostics, like-for-like within-run regression ratios, scheduled automation, and JSON/Markdown/CSV artifacts.
- Executable architecture tests for module dependencies, cycles, namespace ownership, public API boundaries, naming rules, and policy-backed CI/release enforcement.

### Changed

- Added formal project governance, a Contributor License Agreement, repository-wide CODEOWNERS review ownership, and contributor acknowledgements so independent/commercial forks remain permitted while official upstream merges, releases, relicensing, and project identity stay under Project Owner authority.
- Fixed the README banner and legal-document links to use branch-relative repository paths so they render correctly on `develop`, tags, and forks instead of depending on files already existing on `main`.
- Relicensed the current development line under GNU LGPL v3.0 only (`LGPL-3.0-only`) and added a separate TCJ trademark/brand policy so software freedoms and project identity are governed independently. Previously distributed MIT-licensed copies retain the permissions granted with those copies.
- Updated current and published release metadata so package verification enforces the correct SPDX license expression for each release generation (`MIT` for the immutable published preview and `LGPL-3.0-only` for the current development line).
- Opened the `0.1.0-preview.2` development cycle with immutable published-release metadata separated from mutable next-release metadata and release preflight/tag publication requiring an explicitly `ready` release manifest.
- Mutation testing now uses the xUnit v3 MTP runner, validates execution health, and runs before baseline enforcement so the first baseline can be bootstrapped without a CI deadlock.
- Architecture tests now ignore compiler-generated namespace artifacts, recognize the established `Check` guard extension container through policy, and safely inspect constructor and generic method signatures.
- String performance comparisons now use a contract-equivalent BCL baseline, including null validation and runtime inputs, so microbenchmark ratios measure wrapper overhead instead of mismatched work.
- Completed trimming and Native AOT compatibility annotations for convention-based dependency registration and health-check activation without changing the supported AOT boundary.
- Updated EF Core soft-delete query-filter composition to use EF Core 10 query-filter metadata and named filters, preserving existing anonymous/named filters while removing the obsolete `GetQueryFilter()` dependency.

### Fixed

- Moved source-controlled release notes to `docs/release-notes/` so standard Git staging cannot lose them to the Visual Studio `Releases/` ignore rule, and updated documentation validation/repository links accordingly.
- Fixed DocFX site builds by keeping versioned release notes tracked, replacing repository-external relative Markdown links with stable GitHub source links, validating that conceptual links cannot escape the `docs/` content root, and correcting unresolved EF Core `SaveChanges` cref metadata.
- Refined mutation-test scope detection so repository-tool changes trigger Stryker only when the pinned `dotnet-stryker` definition actually changes.
- Fixed Git ignore rules so documentation package landing pages remain tracked and documentation validation verifies their Git status.
- Fixed reproducibility verification for modern `.snupkg` layouts, canonicalized isolated build roots, and handled valid NuGet core-properties/content-type variants without weakening package-content comparison.
- Fixed SBOM generation for multi-targeted packages whose dependency ranges legitimately differ between target-framework groups, while retaining conflict detection inside each group and preserving the original generation failure when verification cannot proceed.
- Pinned the test-only `SSH.NET` dependency to patched version `2026.0.0` for Testcontainers-based suites, keeping it private from TCJ runtime package consumers.
- Replaced timing-dependent SQL Server same-`DbContext` concurrency testing with a deterministic command gate so the expected concurrency violation is reproducible instead of scheduler-dependent.

### Upgrade notes

- The validated `0.1.0-preview.1` to `0.1.0-preview.2` upgrade path requires no consumer source changes.
- Keep all referenced TCJ packages on the same version when upgrading.
- `TCJ.DependencyInjection` adds health-check-related transitive dependencies as part of the new health-check integration.
- Transactional outbox support is opt-in; enabling it requires an explicit consumer-controlled EF Core migration.
- See [`docs/migrations/0.1.0-preview.1-to-0.1.0-preview.2.md`](docs/migrations/0.1.0-preview.1-to-0.1.0-preview.2.md) for the complete migration guidance.

## [0.1.0-preview.1] - 2026-08-01

### Added

- Core entities, Result pattern, structured errors, guards, UUID v7 generation, and domain-event contracts.
- Convention-based dependency registration and sequential domain-event dispatching.
- EF Core repositories, specifications, unit of work, auditing, soft delete, seeding, and entity search.
- SQL Server provider registration, retry options, and rowversion conventions.
- ASP.NET Core current-user resolution, Problem Details, Result mapping, and exception handling.
- Product API sample.
- Cross-package xUnit v3 test projects.
- Shared NuGet packaging metadata and symbol-package settings.
- Comprehensive project, package, architecture, contribution, support, security, and versioning documentation.
- GitHub issue forms and pull-request template.
- CI validation, package-content verification, release preflight, Trusted Publishing, GitHub Releases, and weekly Dependabot updates.

[Unreleased]: https://github.com/Amir-ESH/TCJ.Framework/compare/v0.1.0-preview.2...develop
[0.1.0-preview.2]: https://github.com/Amir-ESH/TCJ.Framework/releases/tag/v0.1.0-preview.2
[0.1.0-preview.1]: https://github.com/Amir-ESH/TCJ.Framework/releases/tag/v0.1.0-preview.1
