# TCJ Framework release checklist

This checklist prepares the next preview, currently `0.1.0-preview.2`. Do not create a tag while `eng/release-manifest.json` has `status: development`.

## Development freeze

- [ ] The intended changes are merged into `develop`.
- [ ] `[Unreleased]` contains complete user-facing notes.
- [ ] Public API changes and migration notes are documented.
- [ ] Package validation is green against the version in `eng/PackageValidation.props`.
- [ ] Dependency review and NuGet Audit are green with the repository security policy unchanged.
- [ ] Release-integrity, SBOM, and reproducibility configuration validation are green.
- [ ] Deterministic compilation, portable PDBs, CI metadata, Source Link, and repository path mapping remain enabled centrally.
- [ ] SBOM policy still requires all five release packages, dependency coverage, hashes, licenses, repository identity, commit SHA, and release version.
- [ ] Line and branch coverage gates are green and the summary was reviewed.
- [ ] The mutation baseline is recorded, the mutation quality gate is green, and survived mutants were reviewed.
- [ ] The latest full performance benchmark run is green and runtime/allocation regressions were reviewed.
- [ ] Architecture policy validation and all `Category=Architecture` tests are green.
- [ ] Any compatibility suppression is minimal, reviewed, and described in the changelog.
- [ ] The published-package smoke workflow is green for the previous release.
- [ ] SQL Server integration policy validation is green and the container image remains pinned.
- [ ] ASP.NET Core integration policy validation is green; Production/Development, current-user isolation, and sanitized diagnostics requirements remain enforced.

## Prepare release metadata

- [ ] `eng/Packaging.props` contains the intended version.
- [ ] `eng/PackageValidation.props` matches `eng/published-release.json`.
- [ ] `eng/release-manifest.json` contains the same version and matching `v` tag.
- [ ] Set manifest `status` to `ready`.
- [ ] Set manifest `releaseDate` to `YYYY-MM-DD`.
- [ ] Move `[Unreleased]` entries into a dated version section in `CHANGELOG.md`.
- [ ] Add `docs/release-notes/<version>.md`.

## Repository and GitHub

- [ ] The release pull request from `develop` to `main` is squash-merged.
- [ ] `Build, test and pack` is successful on the exact `main` commit.
- [ ] The `nuget-production` environment and `NUGET_USER` variable still exist.
- [ ] The NuGet.org Trusted Publishing policy still targets `release.yml` and `nuget-production`.
- [ ] GitHub Dependency graph is enabled and the Dependency review workflow is active.
- [ ] The `Mutation testing / Run mutation tests` check is required for `develop` and `main`, with no administrator bypass.
- [ ] The latest `Reproducible builds / Compare package builds` run is green for the exact release source and SDK.
- [ ] `SQL Server integration / Run database tests` is green for the exact release source.
- [ ] `ASP.NET Core integration / Test on Linux`, `Test on Windows`, and cross-platform verification are green for the exact release source.

## Release candidate

- [ ] Run **Actions → Release preflight → Run workflow** from `main`.
- [ ] Use package-ID policy `existing`.
- [ ] `Validate release candidate` succeeds.
- [ ] Review the release-preflight coverage summary and Cobertura artifact.
- [ ] Review the latest mutation summary and both HTML/JSON mutation reports.
- [ ] Review the latest performance summary plus JSON, Markdown, CSV, and log artifacts.
- [ ] Review the architecture-test summary and confirm no policy weakening was introduced.
- [ ] Review `SQLSERVER_INTEGRATION_SUMMARY.md`, TRX results, and sanitized SQL Server diagnostics; migrations and transaction scenarios are green.
- [ ] Review `ASPNETCORE_INTEGRATION_SUMMARY.md`, both platform TRX files, and sanitized host/HTTP diagnostics; Production safety and current-user/request-scope isolation are green.
- [ ] Review `REPRODUCIBILITY_SUMMARY.md`, both five-package build sets, and any focused difference reports.
- [ ] Confirm assemblies, portable PDBs, Source Link metadata, XML documentation, NuSpec metadata, and extracted package contents match.
- [ ] Confirm any raw archive-only warning is explained by an approved container normalization.
- [ ] Confirm the release candidate under `artifacts/packages` is the promoted verified Build A set.
- [ ] Review five `.nupkg`, five `.snupkg`, the CycloneDX SBOM, `SHA256SUMS`, release notes, and the manifest.
- [ ] Confirm all five TCJ packages, direct/transitive dependencies, hashes, licenses, repository metadata, and the source commit appear in the SBOM.
- [ ] Verify the release-candidate checksums include the SBOM before tagging.

## Publish

- [ ] Create the version tag on the reproducibility-verified `main` commit.
- [ ] Confirm the tag workflow completes reproducibility comparison before checksums, SBOM generation, attestation, or publication.
- [ ] Approve the `nuget-production` deployment after validation succeeds.
- [ ] Confirm all five versions are listed on NuGet.org.
- [ ] Confirm the GitHub pre-release contains ten package assets, one versioned CycloneDX SBOM, and `SHA256SUMS`.
- [ ] Confirm GitHub shows provenance attestations for the package assets, SBOM, and checksum manifest.
- [ ] Verify at least one release asset with `gh attestation verify`.
- [ ] Never move the tag or republish different bits with the same version.

## Post-release reset

- [ ] Copy the released version, tag, and date to `eng/published-release.json`.
- [ ] Move `TCJPublishedPackageVersion` in `eng/PackageValidation.props` to the new release.
- [ ] Increment `eng/Packaging.props` and `eng/release-manifest.json` to the next preview.
- [ ] Set manifest `status` to `development` and `releaseDate` to `null`.
- [ ] Start a fresh `[Unreleased]` section.
- [ ] Run **Published package smoke tests** for the new public version.
- [ ] Verify Source Link from at least one package in a consumer debugger.

## Package consumer compatibility

- [ ] `python3 eng/verify-consumer-compatibility.py validate-config` passes.
- [ ] No compatibility consumer contains a `ProjectReference` or a path into `src/`.
- [ ] `Package consumer compatibility / Test consumers (ubuntu-latest)` is green for the exact release source.
- [ ] `Package consumer compatibility / Test consumers (windows-latest)` is green for the exact release source.
- [ ] `Package consumer compatibility / Test consumers (macos-latest)` is green for the exact release source.
- [ ] Cross-platform compatibility verification is green and the required architecture/TFM policy is unchanged or explicitly reviewed.
- [ ] Release preflight runs the exact promoted candidate package bytes through all six consumers.
- [ ] Review `COMPATIBILITY_SUMMARY.md`, restore/build/runtime logs, resolved versions, and source identity.
- [ ] All five `.nupkg` and `.snupkg` files pass XML documentation, portable PDB, repository metadata, and Source Link validation.
- [ ] The tagged release repeats the exact-package consumer check before NuGet publication and retains compatibility reports with release metadata.
- [ ] After publication, Core, ASP.NET Core, and full-stack consumers restore the released version from NuGet.org.
- [ ] No generated `artifacts/compatibility/` or compatibility `bin`/`obj` output is committed.

## Package upgrade compatibility

- [ ] `eng/published-release.json` identifies the supported upgrade baseline.
- [ ] `eng/release-manifest.json` identifies the exact release-candidate target.
- [ ] All six direct upgrade scenarios restore, build, and run without source changes unless an approved breaking change explicitly requires migration.
- [ ] Baseline TCJ packages are verified as NuGet.org restores and target TCJ packages are verified as exact candidate-feed restores.
- [ ] Dependency diffs contain no downgrade, missing runtime asset, or unexplained target-framework change.
- [ ] Normalized runtime behavior has no unexpected regression.
- [ ] `eng/breaking-changes.json` and the version-specific migration guide agree.
- [ ] Guided migration patches pass when required.
- [ ] Release-preflight upgrade reports are archived.
- [ ] Post-publication Core, ASP.NET Core, and FullStack upgrades pass with the target restored from NuGet.org.

## Property and fuzz testing

- [ ] `python3 eng/verify-fuzzing.py validate-config` passes.
- [ ] `Property and fuzz testing / Run property tests` is green with all required categories and at least 100 generated cases per property.
- [ ] `Property and fuzz testing / Run short fuzz targets` is green for all five required targets.
- [ ] Review deterministic seeds, shrinking/replay output, `PROPERTY_TEST_SUMMARY.md`, and the property TRX artifact.
- [ ] Review `FUZZ_SUMMARY.md`; crashes, hangs, unexpected exceptions, invariant violations, unresolved failures, input-size violations, and timeout violations are zero.
- [ ] Any confirmed finding has a minimized reproducer, linked issue/PR, and conventional regression test.
- [ ] Seed corpora remain small, reviewed, tracked, and free of sensitive values.
- [ ] Generated fuzz corpus, failures, minimized outputs, `bin`, `obj`, and `artifacts/fuzzing/` are not committed.
- [ ] The latest weekly long fuzz campaign is reviewed for the release source lineage.
- [ ] Release preflight archives property/fuzz reports and blocks on unresolved findings.
- [ ] The exact tagged source reruns the property suite and all required fuzz targets before NuGet publication.

## API documentation

- [ ] `dotnet tool restore` restores the pinned DocFX version.
- [ ] `python3 eng/verify-documentation.py validate-config` passes.
- [ ] DocFX metadata is generated for all five production packages.
- [ ] The documentation site builds with warnings treated as errors.
- [ ] Public API documentation coverage meets the measured policy threshold.
- [ ] No new missing summary, parameter, type-parameter, or return documentation is outside the approved baseline.
- [ ] Unresolved `cref` references and broken internal links are zero.
- [ ] Required C# examples compile against the release-candidate source.
- [ ] `DOCUMENTATION_SUMMARY.md` and the Pages-ready site archive are present in workflow artifacts.
- [ ] The tagged release attaches the documentation ZIP generated from the same commit.


## Concurrency stress testing

- [ ] `python3 eng/verify-concurrency.py validate-config` passes.
- [ ] `Concurrency stress / Run core stress tests` is green for the release source.
- [ ] `Concurrency stress / Run ASP.NET Core stress tests` is green and request/current-user isolation is preserved.
- [ ] `Concurrency stress / Run SQL Server stress tests` is green with rollback, unique-constraint, optimistic-concurrency, and independent-transaction coverage.
- [ ] Review deterministic seeds, scenario/operation timeouts, replay metadata, and any failure traces.
- [ ] No failed stress test was converted to success by retry.
- [ ] `DbContext` and Unit of Work remain documented as single-operation/not-concurrently-safe boundaries.
- [ ] Release metadata contains `TCJ.Framework.Concurrency.Evidence.<version>.zip`.
- [ ] Generated concurrency output is not committed.

## Observability

- [ ] `python3 eng/verify-observability.py validate-config` passes.
- [ ] `TCJ.Observability.Tests` passes and `verify-observability.py verify` reports PASS.
- [ ] ActivitySource/Meter versions match the release package version.
- [ ] Sensitive marker scan passes and generated observability artifacts contain no secrets.
- [ ] Telemetry contract changes are intentional, documented, and included in release notes.
- [ ] The observability overhead benchmark is present in the performance evidence.
- [ ] No exporter or vendor telemetry dependency entered a production TCJ package.

## Resilience

- [ ] `python3 eng/verify-resilience.py validate-config` passes.
- [ ] Fast and SQL Server `TCJ.Resilience.Tests` scenarios pass.
- [ ] `verify-resilience.py verify` reports PASS with deterministic attempt traces.
- [ ] Retry/timeout/circuit defaults and telemetry contracts match `eng/resilience-contract.json`.
- [ ] Performance evidence includes the resilience success path and failure-path benchmarks.
- [ ] Generated `TestResults/Resilience/` and `artifacts/resilience/` output is not committed.
