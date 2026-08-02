# Release automation

TCJ Framework publishes from Git tags through `.github/workflows/release.yml`.
The workflow uses NuGet.org Trusted Publishing and GitHub OIDC. It does not require a long-lived NuGet API key.

## Release guarantees

A release is rejected unless all of the following are true:

- the tag starts with `v` and contains a valid semantic version;
- the tag version matches both `eng/Packaging.props` and `eng/release-manifest.json`;
- the changelog contains a dated section for that version;
- the tagged commit is reachable from `main`;
- the repository dependency-security policy is intact and restore completes its direct/transitive NuGet audit;
- restore, Release build, tests, coverage quality gate, and pack succeed;
- the packed APIs remain compatible with the latest published TCJ baseline, except for reviewed suppressions;
- exactly five `.nupkg` and five `.snupkg` files are produced;
- each package contains the expected ID, version, repository metadata, MIT license expression, README, license file, assembly, and portable symbols.

After validation, the protected `nuget-production` environment publishes the packages to NuGet.org and creates a GitHub Release with package assets and notes extracted from `CHANGELOG.md`.

## One-time GitHub configuration

### Allow the NuGet login action

The repository limits workflows to selected actions. Go to:

```text
Settings → Actions → General → Actions permissions
```

Keep GitHub-created actions enabled and add this allowed action pattern:

```text
NuGet/login@v1
```

The workflows also use these GitHub-owned actions:

```text
actions/checkout@v6
actions/setup-dotnet@v6
actions/upload-artifact@v7
actions/download-artifact@v8
actions/dependency-review-action@v4
```

### Create the release environment

Go to:

```text
Settings → Environments → New environment
```

Create:

```text
nuget-production
```

Recommended protection:

- add yourself as the required reviewer;
- leave **Prevent self-review** disabled while you are the only maintainer;
- restrict deployment branches and tags to protected branches and release tags when that option is available.

Create an environment variable:

```text
Name: NUGET_USER
Value: your NuGet.org profile username, not your email address
```

No NuGet API-key secret is needed.

## One-time NuGet.org configuration

Sign in to NuGet.org and open:

```text
Account → Trusted Publishing → Add policy
```

Create a GitHub Actions policy with these exact values:

```text
Policy owner: your individual NuGet.org account
Repository owner: Amir-ESH
Repository: TCJ.Framework
Workflow file: release.yml
Environment: nuget-production
```

Enter only `release.yml` as the workflow filename, not the `.github/workflows/` path.

## Release manifest

The release-specific values are stored in:

```text
eng/release-manifest.json
```

For every new version, update these fields together:

```text
status
version
tag
releaseDate
packages
```

Normal development uses `status: development` and `releaseDate: null`. Both release workflows call `eng/verify-release.py --require-ready`, so publication is blocked until the manifest is explicitly finalized.

`eng/verify-release.py` ensures the manifests, MSBuild version, package-validation baseline, package projects, changelog, documentation, and built packages agree.

## Run release preflight

After the release pull request is merged into `main`, open:

```text
Actions → Release preflight → Run workflow
```

Select branch:

```text
main
```

For all releases after the first publication choose:

```text
package-id-policy: existing
```

`available` is only for claiming package IDs during the first publication. `report-only` reports the NuGet.org state without enforcing it.

The preflight workflow:

1. requires `main`;
2. verifies release metadata and the dated changelog section;
3. queries NuGet.org for all five package IDs;
4. validates dependency-security configuration and audits the complete restored graph;
5. builds, tests, merges Cobertura reports, and enforces line and branch coverage minimums;
6. packs and runs SDK API compatibility validation;
7. inspects the actual `.nupkg` and `.snupkg` contents;
8. uploads the release candidate, test results, and coverage reports.

Download and review the `release-candidate-*` artifact before tagging. The first publication cannot claim a package ID that already exists on NuGet.org under another owner.

## Publish the first preview

Complete [`RELEASE_CHECKLIST.md`](../RELEASE_CHECKLIST.md), then merge `develop` into `main` through a protected pull request and confirm CI and preflight are green on the exact release commit.

Create the annotated tag:

```bash
git switch main
git pull --ff-only

git tag -a v0.1.0-preview.2 \
  -m "TCJ Framework 0.1.0-preview.2"

git push origin v0.1.0-preview.2
```

A version containing a pre-release suffix, such as `-preview.1`, creates a GitHub pre-release automatically.

## Publication sequence

The release workflow performs these operations:

1. validates the tag, manifest, package version, and changelog;
2. builds, tests, and enforces the code coverage policy for the complete solution;
3. creates and deeply inspects all primary and symbol packages;
4. extracts release notes from the matching changelog section;
5. pauses for the `nuget-production` environment approval;
6. generates and verifies `SHA256SUMS` for all primary and symbol packages;
7. creates signed GitHub build-provenance attestations for the packages and checksum manifest;
8. pauses for the `nuget-production` environment approval;
9. exchanges the GitHub OIDC token for a short-lived NuGet API key;
10. re-verifies downloaded artifacts and publishes all packages and associated symbol packages;
11. creates the GitHub pre-release and attaches `.nupkg`, `.snupkg`, and `SHA256SUMS` assets.

`--skip-duplicate` allows a safe rerun after a partial NuGet.org outage. The immutable tag guarantees that reruns use the same source commit and package version.

## Failed release

Do not move or recreate an existing public release tag with different content.

If no packages were published, delete the failed tag, correct the release commit, and create the tag again.

If any package was published, increment the version, update `eng/Packaging.props`, `eng/release-manifest.json`, and `CHANGELOG.md`, then publish a new tag. NuGet package versions are immutable.


## After publication

1. update `eng/published-release.json` to the immutable version and tag that reached NuGet.org;
2. update `TCJPublishedPackageVersion` in `eng/PackageValidation.props` to that same version;
3. increment `eng/Packaging.props` and `eng/release-manifest.json`;
4. set the release manifest to `status: development` and `releaseDate: null`;
5. add the first new entries under `[Unreleased]`;
6. run the **Published package smoke tests** workflow for the released version.

The smoke workflow verifies NuGet registration metadata, public listing state, downloaded package contents, dependency restore, application build, and runtime registration on Linux and Windows.

Verify the GitHub release checksum manifest and at least one artifact attestation after publication. The exact commands and the distinction between GitHub release assets and NuGet.org repository-signed packages are documented in [Release integrity and build provenance](release-integrity.md).

Package validation details and the intentional-breaking-change process are documented in [Public API compatibility](api-compatibility.md).

Dependency audit thresholds, source mapping, and advisory handling are documented in [Dependency and supply-chain security](dependency-security.md).

Code coverage collection, thresholds, and merged-report semantics are documented in [Code coverage quality gate](code-coverage.md).
