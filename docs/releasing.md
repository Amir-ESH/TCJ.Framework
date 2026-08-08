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
- each package contains the expected ID, version, repository metadata, MIT license expression, README, license file, assembly, and portable symbols;
- one versioned CycloneDX JSON SBOM contains all five TCJ packages, restored direct/transitive dependencies, dependency relationships, licenses, hashes, repository identity, release metadata, and source commit;
- `SHA256SUMS` covers all ten package files and the SBOM.

After validation, the protected `nuget-production` environment publishes the packages to NuGet.org and creates a GitHub Release with package assets, the SBOM, checksums, and notes extracted from `CHANGELOG.md`. Step 32 requires no additional GitHub secret, environment, Ruleset, or permission change beyond the existing Release workflow attestation permissions.

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
6. creates isolated Build A and Build B outputs and packs all five primary and symbol packages from each;
7. compares extracted package contents, assemblies, portable PDBs, Source Link, XML documentation, sources, and NuGet metadata;
8. promotes the exact verified Build A package set and runs SDK API compatibility/package inspection against it;
9. generates and validates the CycloneDX SBOM from the verified package metadata and restored production dependencies;
10. includes the SBOM in `SHA256SUMS`;
11. uploads both reproducibility build sets, comparison reports, the complete release candidate, SBOM summaries, test results, and coverage reports.

Download and review the `release-candidate-*` artifact before tagging. The first publication cannot claim a package ID that already exists on NuGet.org under another owner.

## Publish the first preview

Complete [`RELEASE_CHECKLIST.md`](https://github.com/Amir-ESH/TCJ.Framework/blob/develop/RELEASE_CHECKLIST.md), then merge `develop` into `main` through a protected pull request and confirm CI and preflight are green on the exact release commit.

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
3. creates two isolated builds of all primary and symbol packages;
4. blocks on unexplained package-content, assembly, PDB, Source Link, XML documentation, source, or NuGet metadata differences;
5. promotes the exact verified Build A package set and deeply inspects it;
6. generates and strictly verifies the versioned CycloneDX JSON SBOM from that verified set;
7. extracts release notes from the matching changelog section;
8. generates and verifies `SHA256SUMS` for the verified package files and the SBOM;
9. creates signed GitHub build-provenance attestations for the verified packages, SBOM, and checksum manifest;
10. transfers packages, release metadata, and SBOM to the protected publish job;
11. restores dependency metadata and re-verifies the downloaded SBOM and checksums;
12. pauses for the `nuget-production` environment approval;
13. exchanges the GitHub OIDC token for a short-lived NuGet API key;
14. publishes all packages and associated symbol packages;
15. creates the GitHub pre-release and attaches `.nupkg`, `.snupkg`, the versioned `.cdx.json`, and `SHA256SUMS` assets.

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

Verify the GitHub release checksum manifest, inspect the CycloneDX SBOM, and verify at least one package attestation plus the SBOM attestation after publication. The exact commands and the distinction between GitHub release assets and NuGet.org repository-signed packages are documented in [Release integrity and build provenance](release-integrity.md).

Package validation details and the intentional-breaking-change process are documented in [Public API compatibility](api-compatibility.md).

Dependency audit thresholds, source mapping, and advisory handling are documented in [Dependency and supply-chain security](dependency-security.md).

Code coverage collection, thresholds, and merged-report semantics are documented in [Code coverage quality gate](code-coverage.md).


SBOM generation, inspection, checksum, and provenance commands are documented in [Software bill of materials](software-bill-of-materials.md).


Reproducibility commands, isolation rules, approved container normalization, and report interpretation are documented in [Reproducible NuGet package builds](reproducible-builds.md).

## Release documentation artifacts

Release preflight builds DocFX metadata and the complete site from the release-candidate commit, runs the documentation quality gate, and uploads the site, reports, and a versioned ZIP. The tag workflow repeats the process from the exact tagged commit before NuGet publication, attests the documentation archive, and attaches it to the GitHub Release.

Confirm that the documentation summary reports all five packages, the configured coverage threshold, zero unresolved references, zero broken links, successful required snippets, and generated API pages. Documentation created from another commit must not be substituted for the tagged artifact.
