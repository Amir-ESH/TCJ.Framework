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
- exactly six `.nupkg` files are produced for the release package set and five `.snupkg` files are produced for the runtime packages;
- every package contains the expected ID, version, repository metadata, release-manifest license expression, README, and license file; runtime packages also contain their expected assemblies and portable symbols, while tooling packages must satisfy their declared analyzer asset layout and forbidden-runtime-asset rules;
- one versioned CycloneDX JSON SBOM contains all six release packages, restored direct/transitive dependencies, dependency relationships, licenses, hashes, repository identity, release metadata, and source commit;
- `SHA256SUMS` covers all eleven package files and the SBOM.

After validation, the protected `nuget-production` environment publishes the packages to NuGet.org and creates a GitHub Release with package assets, the SBOM, checksums, and notes extracted from `CHANGELOG.md`. the SBOM release gate requires no additional GitHub secret, environment, Ruleset, or permission change beyond the existing Release workflow attestation permissions.

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
releasePackages
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

For normal releases choose the default transition policy:

```text
package-id-policy: transition
```

`transition` requires every package ID recorded in `eng/published-release.json` to already exist on NuGet.org and every newly introduced ID in `eng/release-manifest.json` to remain available. Use `existing` only when every current release package has already been published, `available` only when every current package ID must still be unclaimed, and `report-only` only for diagnostics.

The preflight workflow:

1. requires `main`;
2. verifies release metadata and the dated changelog section;
3. queries NuGet.org for every current release package ID and enforces the selected lifecycle policy;
4. validates dependency-security configuration and audits the complete restored graph;
5. builds, tests, merges Cobertura reports, and enforces line and branch coverage minimums;
6. creates isolated Build A and Build B outputs and packs all six primary packages plus the five runtime symbol packages from each;
7. compares extracted package contents, assemblies, portable PDBs, Source Link, XML documentation, sources, and NuGet metadata;
8. promotes the exact verified Build A package set and runs SDK API compatibility/package inspection against it;
9. generates and validates the CycloneDX SBOM from the verified package metadata and restored production dependencies;
10. includes the SBOM in `SHA256SUMS`;
11. uploads both reproducibility build sets, comparison reports, the complete release candidate, SBOM summaries, test results, and coverage reports.

Download and review the `release-candidate-*` artifact before tagging. The first publication cannot claim a package ID that already exists on NuGet.org under another owner.


### SQL Server integration gate

Release preflight and the tagged release both call `.github/workflows/sqlserver-integration.yml` for the same source commit before package publication can continue. The reusable workflow starts the pinned disposable SQL Server image, migrates isolated databases, runs the `Category=SqlServer` suite, verifies the minimum test count and sanitized diagnostics, and uploads the result artifacts. A failure blocks release readiness or publication. See [SQL Server integration testing](sqlserver-integration-testing.md).


### ASP.NET Core integration gate

Release preflight and the tagged release both call `.github/workflows/aspnetcore-integration.yml` for the exact release source. The reusable workflow runs the in-memory `Category=AspNetCore` suite on Linux and Windows, verifies Production-safe error responses, Development diagnostics, current-user/request-scope isolation, cancellation, sanitized host diagnostics, and the configured minimum test count, then requires a successful cross-platform aggregate before packaging or publication can continue. See [ASP.NET Core end-to-end integration testing](aspnetcore-integration-testing.md).

### Packaged Native AOT release gate

Release preflight and the tagged release run `python3 eng/verify-aot.py verify`, then consume the exact locally packed release-candidate packages through `smoke/TCJ.NativeAot.SmokeTest`. The supported release RID is `linux-x64`. The runner restores `TCJ.Core`, `TCJ.DependencyInjection`, and `TCJ.AspNetCore` only from the local package feed, publishes with `PublishAot=true`, executes the native host, verifies representative DI/domain-event and Minimal API behavior, and confirms that each loaded TCJ assembly reports the candidate `PackageVersion`. Any `IL2xxx`/`IL3xxx` diagnostic fails the Full-support gate; no warning-count baseline is accepted.

The tag workflow retains `native-aot-result.json`, `aot-runtime-verification.json`, and a versioned Native AOT evidence archive with release metadata. The protected publish job downloads the retained result and re-runs `verify-result` against the exact package set before NuGet publication. `TCJ.EntityFrameworkCore` and `TCJ.EntityFrameworkCore.SqlServer` remain separately Experimental and are not promoted by this gate. See [Native AOT and trimming compatibility](guides/native-aot-and-trimming.md).

### Package consumer compatibility gate

Release preflight and the tagged release both depend on `.github/workflows/consumer-compatibility.yml`. That reusable gate packs the release-manifest version and requires all six package-only consumers to restore, build, and run on Linux, Windows, and macOS with exact version/source verification. After reproducible Build A is promoted, the release job additionally copies those exact verified package bytes into the local compatibility feed and runs all six consumers again on Ubuntu before SBOM/checksum/publication stages can continue. Primary and symbol package metadata, XML documentation, portable PDBs, and Source Link are validated against the release commit, and compatibility summaries are retained with release artifacts. See [Package consumer compatibility](package-consumer-compatibility.md).

## Publish the current preview

Complete `RELEASE_CHECKLIST.md` (repository path: `RELEASE_CHECKLIST.md`), then merge `develop` into `main` through a protected pull request and confirm CI and preflight are green on the exact release commit.

Create the annotated tag:

```bash
git switch main
git pull --ff-only

git tag -a v0.1.0-preview.5 \
  -m "TCJ Framework 0.1.0-preview.5"

git push origin v0.1.0-preview.5
```

A version containing a pre-release suffix, such as `-preview.1`, creates a GitHub pre-release automatically.

## Publication sequence

The release workflow performs these operations:

1. validates the tag, manifest, package version, and changelog;
2. builds, tests, and enforces the code coverage policy for the complete solution;
3. creates two isolated builds of all primary and symbol packages;
4. blocks on unexplained package-content, assembly, PDB, Source Link, XML documentation, source, or NuGet metadata differences;
5. promotes the exact verified Build A package set and deeply inspects it;
6. publishes and executes the packed `linux-x64` Native AOT smoke, verifies exact loaded TCJ versions and zero trim/AOT warning baseline, and retains the result;
7. generates and strictly verifies the versioned CycloneDX JSON SBOM from that verified set;
8. extracts release notes from the matching changelog section;
9. generates and verifies `SHA256SUMS` for the verified package files and the SBOM;
10. creates signed GitHub build-provenance attestations for the verified packages, SBOM, checksum manifest, and retained Native AOT evidence;
11. transfers packages, release metadata, Native AOT evidence, and SBOM to the protected publish job;
12. re-verifies the downloaded Native AOT result against the exact package set, then re-verifies dependency metadata, SBOM, and checksums;
13. pauses for the `nuget-production` environment approval;
14. exchanges the GitHub OIDC token for a short-lived NuGet API key;
15. publishes all packages and associated symbol packages;
16. creates the GitHub pre-release and attaches the package, SBOM, checksum, and Native AOT evidence assets.

`--skip-duplicate` allows a safe rerun after a partial NuGet.org outage. The immutable tag guarantees that reruns use the same source commit and package version.

## Failed release

Do not move or recreate an existing public release tag with different content.

If no packages were published, delete the failed tag, correct the release commit, and create the tag again.

If any package was published, increment the version, update `eng/Packaging.props`, `eng/release-manifest.json`, and `CHANGELOG.md`, then publish a new tag. NuGet package versions are immutable.


## After publication

1. update `eng/published-release.json` to the immutable version, tag, date, package set, and `licenseExpression` that reached NuGet.org;
2. update `TCJPublishedPackageVersion` in `eng/PackageValidation.props` to that same version;
3. increment `eng/Packaging.props` and `eng/release-manifest.json`;
4. set the release manifest to `status: development` and `releaseDate: null`;
5. add the first new entries under `[Unreleased]`;
6. merge the post-release reset into `develop`, then synchronize the public/default `main` branch with the safe post-release metadata and documentation state through a protected maintenance pull request; do not create or move a release tag for this synchronization;
7. confirm the `main` README reports the newly published version and that scheduled maintenance cannot fall back to the previous published baseline;
8. run the **Published package smoke tests** workflow for the released version.

The release tag, not the moving `main` branch, is the immutable source snapshot for a published version. Keeping the default branch's landing page and maintenance metadata current prevents users and scheduled workflows from observing the previous release after publication. Runtime feature development still targets `develop`; the post-release `main` synchronization is limited to the already-reviewed reset/documentation/maintenance state.

The smoke workflow verifies NuGet registration metadata, public listing state, and downloaded package contents, then reuses the maintained Core, ASP.NET Core, and full-stack compatibility consumers against NuGet.org on Linux, Windows, and macOS. The package source, exact released version, application build, HTTP startup, EF/SQL Server registration, and runtime execution must all pass.

Verify the GitHub release checksum manifest, inspect the CycloneDX SBOM, and verify at least one package attestation plus the SBOM attestation after publication. The exact commands and the distinction between GitHub release assets and NuGet.org repository-signed packages are documented in [Release integrity and build provenance](release-integrity.md).

Package validation details and the intentional-breaking-change process are documented in [Public API compatibility](api-compatibility.md).

Dependency audit thresholds, source mapping, and advisory handling are documented in [Dependency and supply-chain security](dependency-security.md).

Code coverage collection, thresholds, and merged-report semantics are documented in [Code coverage quality gate](code-coverage.md).


SBOM generation, inspection, checksum, and provenance commands are documented in [Software bill of materials](software-bill-of-materials.md).


Reproducibility commands, isolation rules, approved container normalization, and report interpretation are documented in [Reproducible NuGet package builds](reproducible-builds.md).

## Release documentation artifacts

Release preflight builds DocFX metadata and the complete site from the release-candidate commit, runs the documentation quality gate, and uploads the site, reports, and a versioned ZIP. The tag workflow repeats the process from the exact tagged commit before NuGet publication, attests the documentation archive, and attaches it to the GitHub Release.

Confirm that the documentation summary reports all five packages, the configured coverage threshold, zero unresolved references, zero broken links, successful required snippets, and generated API pages. Documentation created from another commit must not be substituted for the tagged artifact.

## Upgrade compatibility gate

Release preflight and the official tag workflow run all six upgrade scenarios from the version in `eng/published-release.json` to the version in `eng/release-manifest.json` using the exact promoted candidate packages. Undocumented behavior changes, dependency downgrades, source-tree changes, or incomplete guided migrations block release. After NuGet publication, the published-package workflow reruns Core, ASP.NET Core, and FullStack upgrade paths with the target restored from NuGet.org. See [package upgrade testing](package-upgrade-testing.md) and the [current migration guide](migrations/0.1.0-preview.4-to-0.1.0-preview.5.md).

## Property and fuzz release gate

Release preflight and the official tag workflow rerun all required property tests and fuzz targets against the exact release source. Any unresolved property failure, crash, hang, unexpected exception, invariant violation, or resource-limit violation blocks publication. Review `PROPERTY_TEST_SUMMARY.md`, `FUZZ_SUMMARY.md`, and any minimized failure corpus before approving a release.


## Concurrency stress release gate

Release preflight and official tagged releases execute the reusable `Concurrency stress` workflow against the exact release source. Core, ASP.NET Core request-isolation, and SQL Server transaction scenarios must all pass with deterministic seeds and without unresolved failure traces. A deadlock, hang, timeout, duplicate/missing operation, scope/identity leak, or transaction interference blocks release readiness and NuGet publication. The tag workflow downloads commit-matched concurrency artifacts and retains the summaries/traces in `TCJ.Framework.Concurrency.Evidence.<version>.zip` with release metadata.

## Observability release gate

Release preflight and the official tag workflow validate the committed observability policy, execute `TCJ.Observability.Tests`, verify source/meter version metadata, scan generated telemetry evidence for the synthetic sensitive-data markers, publish `OBSERVABILITY_SUMMARY.md`, and block the release when the telemetry contract drifts. Exporter packages remain sample/application dependencies rather than production TCJ dependencies.

### Resilience release gate

Release preflight and official tag publication call the reusable resilience workflow for the exact source commit. It validates the committed policy/contract, executes deterministic core and SQL Server fault-injection scenarios, verifies attempt traces, and publishes `RESILIENCE_SUMMARY.md`. Unresolved retry, timeout, circuit, transaction, duplicate-side-effect, telemetry, or trace failures block publication. The post-publication smoke workflow detects packages exposing the resilience feature set and runs a selected retry/classification scenario against the NuGet package set.

## Health-check release gate

Release preflight and official tag publication depend on the commit-matched `Health checks` reusable workflow. The release preserves the generated `HEALTH_CHECK_SUMMARY.md` evidence, and published-package validation maps and executes the released liveness/readiness endpoint APIs.

## Transactional outbox release evidence

Release preflight and the official tag workflow invoke the reusable `outbox.yml` gate and validate `eng/outbox-policy.json` / `eng/outbox-contract.json` against the exact source. Publication must not proceed when transaction consistency, SQL Server claim concurrency, retry/dead-letter, replay/cleanup, sensitive-data, telemetry, health, or contract verification fails. The `OUTBOX_SUMMARY.md` and sanitized JSON reports are retained with release evidence.
