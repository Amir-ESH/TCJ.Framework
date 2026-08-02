# Changelog

All notable changes to TCJ Framework will be documented in this file.

The project follows semantic versioning. Until `1.0.0`, preview releases may include breaking public API changes.

## [Unreleased]

### Added

- SDK package validation against the latest published TCJ package baseline.
- CI enforcement for accidental binary-breaking public API changes.
- Public API compatibility policy and maintainer workflow.
- Automated verification of listed NuGet packages using the NuGet V3 registration and flat-container APIs.
- Cross-platform smoke consumer that restores, builds, and runs all published TCJ packages from NuGet.org.
- Weekly and manually triggered published-package smoke-test workflow.
- Repository-wide NuGet Audit policy for direct and transitive dependencies.
- Pull-request dependency review and scheduled dependency-audit workflows.
- Explicit NuGet.org package-source mapping and audit-source configuration.

### Changed

- Opened the next development cycle as `0.1.0-preview.2`.
- Separated immutable published-release metadata from mutable next-release metadata.
- Release preflight and tag publication now require an explicitly `ready` release manifest.

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
