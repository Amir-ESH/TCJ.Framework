# TCJ Framework release checklist

This checklist prepares the next preview, currently `0.1.0-preview.2`. Do not create a tag while `eng/release-manifest.json` has `status: development`.

## Development freeze

- [ ] The intended changes are merged into `develop`.
- [ ] `[Unreleased]` contains complete user-facing notes.
- [ ] Public API changes and migration notes are documented.
- [ ] Package validation is green against the version in `eng/PackageValidation.props`.
- [ ] Dependency review and NuGet Audit are green with the repository security policy unchanged.
- [ ] Release-integrity configuration validation is green.
- [ ] Line and branch coverage gates are green and the summary was reviewed.
- [ ] The recorded mutation baseline is valid, the mutation quality gate is green, and survived mutants were reviewed.
- [ ] Any compatibility suppression is minimal, reviewed, and described in the changelog.
- [ ] The published-package smoke workflow is green for the previous release.

## Prepare release metadata

- [ ] `eng/Packaging.props` contains the intended version.
- [ ] `eng/PackageValidation.props` matches `eng/published-release.json`.
- [ ] `eng/release-manifest.json` contains the same version and matching `v` tag.
- [ ] Set manifest `status` to `ready`.
- [ ] Set manifest `releaseDate` to `YYYY-MM-DD`.
- [ ] Move `[Unreleased]` entries into a dated version section in `CHANGELOG.md`.
- [ ] Add `docs/releases/<version>.md`.

## Repository and GitHub

- [ ] The release pull request from `develop` to `main` is squash-merged.
- [ ] `Build, test and pack` is successful on the exact `main` commit.
- [ ] The `nuget-production` environment and `NUGET_USER` variable still exist.
- [ ] The NuGet.org Trusted Publishing policy still targets `release.yml` and `nuget-production`.
- [ ] GitHub Dependency graph is enabled and the Dependency review workflow is active.

## Release candidate

- [ ] Run **Actions → Release preflight → Run workflow** from `main`.
- [ ] Use package-ID policy `existing`.
- [ ] `Validate release candidate` succeeds.
- [ ] Review the release-preflight coverage summary and Cobertura artifact.
- [ ] Review the latest mutation summary plus both HTML/JSON mutation reports; do not release with a pending baseline.
- [ ] Review five `.nupkg`, five `.snupkg`, `SHA256SUMS`, release notes, and the manifest.
- [ ] Verify the release-candidate checksums before tagging.

## Publish

- [ ] Create the version tag on the verified `main` commit.
- [ ] Approve the `nuget-production` deployment after validation succeeds.
- [ ] Confirm all five versions are listed on NuGet.org.
- [ ] Confirm the GitHub pre-release contains ten package assets and `SHA256SUMS`.
- [ ] Confirm GitHub shows provenance attestations for the package assets.
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
