# Reproducible NuGet package builds

Reproducible-build verification checks whether the same source commit, package version, SDK, repository configuration, and dependency state produce equivalent NuGet package contents in two independent builds.

This control complements, but does not replace, other release-integrity controls:

- checksums identify an exact artifact after it has been created;
- attestations record the workflow and source identity that produced an artifact;
- SBOMs describe the components and dependency graph in a release;
- reproducibility checks whether the trusted inputs can produce the same package payload again.

## Policy and verifier

The policy is stored in:

```text
eng/reproducibility-policy.json
```

The verifier is:

```text
eng/verify-reproducible-build.py
```

Validate repository configuration with:

```bash
python3 eng/verify-reproducible-build.py validate-config
```

An exported source archive without `.git` metadata can use:

```bash
python3 eng/verify-reproducible-build.py validate-config --skip-git-check
```

The skip option is only for exported archives. CI intentionally performs the Git checks so it can reject an ignored or untracked policy/verifier.

## Deterministic build settings

`Directory.Build.props` centrally enables:

- deterministic compilation;
- `ContinuousIntegrationBuild` when `CI=true`;
- portable PDB generation;
- embedding of untracked sources;
- deterministic source paths;
- repository-root path mapping to `/_/`.

Production projects inherit these settings. Project-level overrides of deterministic compilation, PDB format, CI metadata, source embedding, or path mapping are rejected by configuration validation.

`eng/Packaging.props` keeps repository metadata and creates portable-PDB `.snupkg` files. The modern symbol-package format may omit physical `src/**/*.cs` entries; source retrieval is instead described by Source Link metadata embedded in each portable PDB. The same `RepositoryCommit` is passed to both builds so Source Link and NuGet repository metadata identify the same commit.

## Run the full comparison locally

Use the SDK selected by `global.json`, a clean working tree, and one package version for both builds:

```bash
set -euo pipefail

version="$(python3 - <<'PY'
import xml.etree.ElementTree as ET
value = ET.parse("eng/Packaging.props").getroot().findtext("./PropertyGroup/Version")
if not value or not value.strip():
    raise SystemExit("eng/Packaging.props does not define Version.")
print(value.strip())
PY
)"
commit="$(git rev-parse HEAD)"

python3 eng/verify-reproducible-build.py validate-config
dotnet restore TCJ.slnx --force-evaluate
rm -rf artifacts/reproducibility

for build in build-a build-b; do
  root="$PWD/artifacts/reproducibility/$build"

  dotnet restore TCJ.slnx \
    --force-evaluate \
    --artifacts-path "$root" \
    -p:ContinuousIntegrationBuild=true \
    -p:RepositoryCommit="$commit"

  dotnet build TCJ.slnx \
    --configuration Release \
    --no-restore \
    --artifacts-path "$root" \
    -p:Version="$version" \
    -p:ContinuousIntegrationBuild=true \
    -p:RepositoryCommit="$commit"

  mkdir -p "$root/packages"
  dotnet pack TCJ.slnx \
    --configuration Release \
    --no-build \
    --no-restore \
    --artifacts-path "$root" \
    -p:PackageOutputPath="$root/packages" \
    -p:Version="$version" \
    -p:ContinuousIntegrationBuild=true \
    -p:RepositoryCommit="$commit"
done

python3 eng/verify-reproducible-build.py compare \
  --version "$version" \
  --build-a artifacts/reproducibility/build-a/packages \
  --build-b artifacts/reproducibility/build-b/packages \
  --output artifacts/reproducibility/report
```

`--artifacts-path` is passed to restore, build, and pack. Each command therefore uses the same isolated output layout for its build while Build A and Build B do not share intermediate or compiled outputs. The NuGet global package cache may be reused; restored project state and build outputs remain isolated.

Generated compiler inputs also live below those different intermediate roots. Each reproducibility workflow therefore passes `ReproducibleBuildRoot` to MSBuild, and `Directory.Build.props` maps both physical roots to the same canonical compiler path (`/_/artifacts/reproducibility/build`) before applying the repository-wide mapping. Without this narrower mapping, `build-a` and `build-b` can be embedded in portable PDB document paths, changing the PDB identity and the deterministic assembly output even though the source commit is identical.

## What is compared

The verifier requires the five runtime `.nupkg` files and their five `.snupkg` files for the configured version, and also compares release tooling `.nupkg` files that use the analyzer-only package layout. For `0.1.0-preview.4`, the runtime set is:

- `TCJ.Core`;
- `TCJ.DependencyInjection`;
- `TCJ.EntityFrameworkCore`;
- `TCJ.EntityFrameworkCore.SqlServer`;
- `TCJ.AspNetCore`.

The release tooling set additionally contains `TCJ.Generators` as a primary `.nupkg` only; it does not require a `.snupkg`.

For each package it validates the NuSpec identity and version, rejects duplicate identities and unsafe ZIP paths, discovers the package layout, and compares canonical extracted file sets and contents.

Blocking comparisons include:

- compiled assemblies;
- portable PDB bytes;
- embedded Source Link document mappings;
- XML documentation;
- any physical source entries when a generated symbol package contains them;
- NuSpec metadata, dependencies, repository URL, and repository commit;
- OPC relationship and content-type metadata;
- every other extracted payload file.

A difference in any blocking category fails verification. Binary reports contain hashes, sizes, and the first differing byte offset rather than dumping binary contents.

A `.snupkg` is required to contain its portable PDBs, but physical source files are not required by the modern NuGet symbol-package format. Source Link document mappings are extracted from every PDB and compared as a blocking check. When a symbol package does include `src/**/*.cs` or another additional payload entry, that entry remains part of the complete extracted file-set comparison, so a content or presence mismatch still fails verification.

## Raw archives and narrow normalization

The verifier reports full `.nupkg` and `.snupkg` SHA-256 equality separately from extracted-content equality. Raw ZIP bytes can differ because ZIP entry timestamps and NuGet OPC core-properties metadata are container details rather than package payload semantics.

Only two normalization rule families are approved:

1. replace the `dcterms:created` value in the NuGet core-properties part with a deterministic comparison value;
2. canonicalize the generated core-properties part name consistently in the ZIP entry, root relationship identifier/target, and the matching `[Content_Types].xml` override when NuGet emits a part-specific override. The equally valid `Default Extension="psmdcp"` representation contains no generated part name and is validated without rewriting.

The original values from both builds remain visible in the JSON and Markdown reports. Raw archive warnings also identify the first changed ZIP entry order or entry timestamp when present. No DLL, PDB, Source Link, NuSpec, XML documentation, optional physical source entry, dependency metadata, repository commit, generated compiler identifier, or package payload path is normalized.

A proposed normalization rule must be narrow, deterministic, documented here, added to `eng/reproducibility-policy.json`, and covered by a failing-then-passing fixture test. Broad XML, binary, path, or timestamp suppression is not acceptable.

## Output and investigation

Generated files are written under:

```text
artifacts/reproducibility/build-a/
artifacts/reproducibility/build-b/
artifacts/reproducibility/report/REPRODUCIBILITY_SUMMARY.md
artifacts/reproducibility/report/reproducibility-summary.json
artifacts/reproducibility/report/differences/
```

The Markdown summary records the commit, package version, SDK, operating system, package counts, every comparison category, raw archive status, approved normalization observations, and the final result.

Focused difference reports include:

- package and entry path;
- Build A and Build B SHA-256 values;
- file sizes;
- the first useful structural difference;
- whether a difference is normalized;
- whether the difference is blocking.

Start with `REPRODUCIBILITY_SUMMARY.md`, then open the package-level report in `differences/`. For a PDB failure, inspect both the PDB report and the Source Link report. For a NuSpec failure, compare package version, dependencies, and repository metadata before reviewing formatting.

## CI and release behavior

The **Reproducible builds** workflow runs:

- manually;
- weekly;
- for pull requests that change source, packaging, build, dependency, SDK, or release configuration.

Documentation-only changes do not trigger the expensive double build. Normal CI validates configuration and runs the synthetic verifier tests.

Release preflight and the tag-based release workflow both:

1. create isolated Build A and Build B outputs;
2. pack all six primary release packages and the five runtime symbol packages from each build;
3. fail on unexplained extracted-content differences;
4. publish the Markdown summary to `$GITHUB_STEP_SUMMARY`;
5. upload both package sets and focused reports;
6. promote the exact Build A package files only after comparison succeeds.

The official release then validates the promoted set, generates its SBOM and checksums, attests it, uploads it as the release artifact, and transfers that same artifact to the protected publication job. Checksums, SBOM generation, attestation, NuGet publication, and GitHub Release creation therefore occur only after reproducibility verification succeeds.

## SDK and compiler changes

Compiler and SDK versions can change assembly metadata, portable PDBs, Source Link encoding, and package layout. `global.json` pins the SDK entry point for both builds. Any SDK change requires a successful fresh double build and explicit review of resulting package-layout or metadata changes; deterministic settings must not be weakened to preserve an old output.

The optional OPC `dcterms:created` property is normalized only when present. A package that omits this optional property is valid and is compared unchanged; a presence mismatch between Build A and Build B remains blocking.
