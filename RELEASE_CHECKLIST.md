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

## Release candidate

- [ ] Run **Actions → Release preflight → Run workflow** from `main`.
- [ ] Use package-ID policy `existing`.
- [ ] `Validate release candidate` succeeds.
- [ ] Review the release-preflight coverage summary and Cobertura artifact.
- [ ] Review the latest mutation summary and both HTML/JSON mutation reports.
- [ ] Review the latest performance summary plus JSON, Markdown, CSV, and log artifacts.
- [ ] Review the architecture-test summary and confirm no policy weakening was introduced.
- [ ] Review `SQLSERVER_INTEGRATION_SUMMARY.md`, TRX results, and sanitized SQL Server diagnostics; migrations and transaction scenarios are green.
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
