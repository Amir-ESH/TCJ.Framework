# Software bill of materials

TCJ Framework publishes a CycloneDX JSON software bill of materials (SBOM) with every official GitHub release. The SBOM is a machine-readable inventory of the five TCJ NuGet packages, their direct and transitive NuGet dependencies, dependency relationships, licenses, package URLs, cryptographic hashes, repository identity, release version, tag, and source commit.

An SBOM answers **what is in the release**. It complements, but does not replace:

- NuGet vulnerability auditing and dependency review, which identify known advisories;
- `SHA256SUMS`, which detects changed release files;
- GitHub artifact attestations, which identify the workflow and repository that produced an artifact;
- package API validation, which detects accidental public compatibility changes.

## Published format and files

The SBOM release gate uses CycloneDX JSON specification `1.6` as the single validated format. For a release version represented by `<version>`, it produces:

```text
artifacts/sbom/TCJ.Framework.<version>.cdx.json
artifacts/sbom/SBOM_SUMMARY.md
artifacts/sbom/sbom-summary.json
```

Only the versioned `.cdx.json` file is a GitHub Release asset. The summary files are workflow artifacts for maintainers. Generated files under `artifacts/sbom/` and all `*.cdx.json` files are ignored by Git; policy and scripts remain tracked.

## What is represented

The SBOM contains library components for:

- `TCJ.Core`;
- `TCJ.DependencyInjection`;
- `TCJ.EntityFrameworkCore`;
- `TCJ.EntityFrameworkCore.SqlServer`;
- `TCJ.AspNetCore`;
- every restored external NuGet dependency reachable from those production projects.

Each primary TCJ `.nupkg` is represented by its TCJ library component and SHA-256 hash. Each `.snupkg` is represented as a file component with its own hash and a relationship to the corresponding TCJ package.

Direct TCJ package relationships are read from the generated `.nuspec` metadata inside each `.nupkg`. Exact external versions and transitive relationships are read from the production projects' `obj/project.assets.json` files after `dotnet restore`. Package hashes and license metadata are read from the locally restored NuGet cache. The dependency graph is therefore derived from generated and restored package metadata rather than copied from a handwritten diagram.

Multi-targeted NuGet packages may declare different version ranges for the same dependency in different NuSpec target-framework groups. The SBOM parser accepts those framework-specific declarations because `project.assets.json` is the source of truth for the version actually restored for TCJ's target framework. Conflicting duplicate ranges inside the same NuSpec dependency group remain invalid and fail generation.

## Policy

`eng/sbom-policy.json` (repository path: `eng/sbom-policy.json`) defines:

- the required CycloneDX format and specification version;
- the exact five release package IDs;
- the repository identity;
- required direct and transitive dependency coverage;
- required SHA-256 hashes and license metadata;
- required repository, commit, release-version, and release-tag metadata.

Validate repository configuration with:

```bash
python3 eng/verify-sbom.py validate-config
```

Policy changes require a technical explanation in the pull request. Do not remove packages, weaken metadata requirements, or exempt dependencies merely to make CI green. A legitimate package-set or dependency-model change must also update release metadata, architecture documentation, package tests, and release documentation.

## Generate locally

Restore, build, and pack the repository first so package metadata, `project.assets.json`, and NuGet cache entries exist:

```bash
dotnet restore TCJ.slnx
dotnet build TCJ.slnx -c Release --no-restore
dotnet pack TCJ.slnx -c Release --no-build
```

Read the current version and generate the SBOM:

```bash
version="$(python3 -c 'import json; print(json.load(open("eng/release-manifest.json"))["version"])')"
commit="$(git rev-parse HEAD)"
tag="$(python3 -c 'import json; print(json.load(open("eng/release-manifest.json"))["tag"])')"

python3 eng/generate-sbom.py \
  --version "$version" \
  --package-directory artifacts/packages \
  --output artifacts/sbom \
  --commit-sha "$commit" \
  --release-tag "$tag"
```

Generation fails rather than silently omitting a package when required `.nupkg`, `.snupkg`, restored dependency, NuGet cache archive, `.nuspec`, hash, or license metadata cannot be resolved.

## Validate locally

```bash
python3 eng/verify-sbom.py verify \
  --version "$version" \
  --package-directory artifacts/packages \
  --sbom "artifacts/sbom/TCJ.Framework.$version.cdx.json" \
  --summary artifacts/sbom/SBOM_SUMMARY.md \
  --json artifacts/sbom/sbom-summary.json
```

Verification rejects malformed or unsupported SBOMs, missing or duplicated TCJ packages, version mismatches, missing package URLs, unrepresented release files, hash mismatches, missing licenses, incomplete direct or transitive dependencies, unresolved dependency references, and missing repository, commit, version, or tag metadata.

## Inspect dependencies and licenses

The `components` array contains package identity, version, purl, SHA-256 hash, license declaration, external references, and TCJ-specific properties. The `dependencies` array contains `ref` and `dependsOn` relationships using the same component `bom-ref` values.

Useful local queries with `jq` include:

```bash
jq '.components[] | select(.type == "library") | {name, version, purl, licenses}' \
  "artifacts/sbom/TCJ.Framework.$version.cdx.json"

jq '.dependencies[] | select(.dependsOn | length > 0)' \
  "artifacts/sbom/TCJ.Framework.$version.cdx.json"
```

A missing license is a release-blocking investigation item under the current policy. Confirm whether the upstream NuGet package declares an SPDX expression, a license file, or a license URL. Do not invent a license or add a broad exception.

## Verify checksums

`eng/release-integrity.py` writes one `SHA256SUMS` entry for each of the five `.nupkg` files, five `.snupkg` files, and the versioned SBOM:

```bash
python3 eng/release-integrity.py write \
  --version "$version" \
  --package-directory artifacts/packages \
  --sbom "artifacts/sbom/TCJ.Framework.$version.cdx.json" \
  --checksums artifacts/release/SHA256SUMS

python3 eng/release-integrity.py verify \
  --version "$version" \
  --package-directory artifacts/packages \
  --sbom "artifacts/sbom/TCJ.Framework.$version.cdx.json" \
  --checksums artifacts/release/SHA256SUMS
```

Consumers can download all GitHub Release assets into one directory and run:

```bash
sha256sum --check SHA256SUMS
```

## Verify provenance

The tagged Release workflow includes the SBOM in the same GitHub artifact attestation subject set as the packages and checksum manifest. Verify the downloaded SBOM with GitHub CLI using the repository identity:

```bash
gh attestation verify "TCJ.Framework.$version.cdx.json" \
  --repo Amir-ESH/TCJ.Framework
```

The publish job downloads the SBOM produced by the build job, restores dependency metadata, verifies the SBOM again, verifies its checksum, and only then uploads it to the GitHub Release.

## CI and release behavior

Normal CI packs local artifacts, generates and verifies an SBOM, includes it in `SHA256SUMS`, publishes the Markdown summary to the job summary, and uploads the JSON and summaries. Nothing is published externally.

Release preflight repeats generation and validation on `main` and uploads the complete candidate set. The official tag workflow additionally attests the SBOM, transfers it between build and publish jobs, re-verifies it after download, and attaches it to the GitHub Release.

## Vulnerability response

During an incident, use the SBOM to identify affected package versions and their dependency paths, then confirm exposure with NuGet advisory data and the application’s actual usage. An SBOM is an inventory snapshot; it does not prove exploitability and is not a replacement for continuous vulnerability scanning, patching, or runtime investigation.
