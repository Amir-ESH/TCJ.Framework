# Changelog

All notable changes to TCJ Framework will be documented in this file.

The project follows semantic versioning. Until `1.0.0`, preview releases may include breaking public API changes.

## [Unreleased]

- Moved source-controlled release notes to `docs/release-notes/` so standard Git staging cannot lose them to the Visual Studio `Releases/` ignore rule.
- Updated documentation validation and repository links to use the non-ignored release-note path.
- Fixed DocFX site builds by keeping versioned release notes tracked and replacing repository-external relative Markdown links with stable GitHub source links.
- Strengthened documentation validation so local conceptual links cannot escape the DocFX `docs/` content root and are checked during `validate-config`.
- Fixed DocFX metadata generation for EF Core save interceptors by replacing inherited external XML comments that produced unresolved `DbContext.SaveChanges` cref values.
- Refined mutation-test scope detection so repository-tool changes trigger Stryker only when the pinned `dotnet-stryker` definition actually changes.

- Fixed Git ignore rules so documentation package landing pages remain tracked and documentation validation verifies their Git status.

- Fixed reproducibility verification to support valid modern `.snupkg` files that contain portable PDBs and Source Link metadata without physical `src/**/*.cs` entries; optional source entries remain fully compared when present.
- Canonicalized isolated reproducibility build roots so generated source paths no longer change portable PDBs or deterministic assemblies.
- Fixed reproducibility verification for NuGet core-properties parts that legitimately omit the optional `dcterms:created` value or register `.psmdcp` through a `[Content_Types].xml` default declaration.
- Fixed SBOM generation for multi-targeted NuGet packages whose dependency version ranges legitimately differ between target-framework groups, while retaining conflict detection inside each individual group.
- Prevented SBOM verification from masking an earlier generation failure.

### Added

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

- Opened the next development cycle as `0.1.0-preview.2`.
- Separated immutable published-release metadata from mutable next-release metadata.
- Release preflight and tag publication now require an explicitly `ready` release manifest.
- Mutation testing now uses the xUnit v3 MTP runner, validates execution health, and runs before baseline enforcement so the first baseline can be bootstrapped without a CI deadlock.
- Architecture tests now ignore compiler-generated namespace artifacts, recognize the established `Check` guard extension container through policy, and safely inspect constructor and generic method signatures.
- String performance comparisons now use a contract-equivalent BCL baseline, including null validation and runtime inputs, so microbenchmark ratios measure wrapper overhead instead of mismatched work.

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

[Unreleased]: https://github.com/Amir-ESH/TCJ.Framework/compare/v0.1.0-preview.1...develop
[0.1.0-preview.1]: https://github.com/Amir-ESH/TCJ.Framework/releases/tag/v0.1.0-preview.1
