# Package upgrade testing

Package upgrade tests answer a different question from public API compatibility: **can an application that works with the published TCJ packages restore, compile, start, and behave correctly after replacing those packages with the release candidate?** API comparison can identify surface changes, but it does not execute NuGet resolution, dependency transitions, service registration, middleware startup, provider wiring, persisted data, or runtime behavior.

## Version and source selection

The baseline is always the version in `eng/published-release.json`; the target is always the version in `eng/release-manifest.json`. The dedicated workflow does not carry a second hard-coded version pair.

The baseline phase uses an isolated NuGet environment and `upgrade-tests/NuGet.Baseline.Config`, which permits only NuGet.org. The target phase uses a separate cache and a generated form of `upgrade-tests/NuGet.Target.Config`: `TCJ.*` maps to the locally packed release-candidate feed and public dependencies map to NuGet.org. NuGet's `.nupkg.metadata` is inspected after restore so source identity is verified rather than inferred from configuration.

## Direct upgrade contract

Each scenario has one source tree and TCJ PackageReferences use `$(TCJUpgradeVersion)`. The runner supplies the baseline version for the first restore/build/run and the target version for the second. It hashes source files before the baseline phase and again after the direct target phase. A changed hash is a failure.

The six scenarios cover Core, Dependency Injection, provider-independent EF Core, SQL Server registration, ASP.NET Core, and all five packages together. `EntityFrameworkCoreConsumer` persists a deterministic record to SQLite during the baseline run and reads that same database during the target run. SQL Server connectivity itself remains the responsibility of the container-backed integration suite.

## Runtime behavior

Each scenario writes a machine-readable `behavior.json`. Values are semantic booleans and deliberately exclude timestamps, ports, machine names, temporary paths, random IDs, and trace identifiers. Baseline and target behavior is classified as `Equivalent`, `Compatible improvement`, `Documented change`, `Intentional breaking change`, `Unexpected regression`, or narrowly documented `Environment noise`. Unexpected regressions are blocking.

## Dependency graph comparison

The runner reads `obj/project.assets.json` for each phase and records package versions plus compile, runtime, build, analyzer, and target-framework assets. Reports show added and removed packages, upgrades and downgrades, asset changes, removed runtime assets, and target-framework selection changes. Dependency differences can be compatible, but downgrades, removed runtime assets, and unexpected target-framework changes block the upgrade.

## Intentional breaking changes and migrations

`eng/breaking-changes.json` is the only approval surface for intentional breaking changes. An entry must set `approved: true`, record the approving maintainer in `approvedBy`, identify affected scenarios, link to a repository issue or pull request, and reference an existing heading in the version-specific migration guide. A source-changing migration additionally declares an explicit target-version patch stored in the repository. The direct result remains visible; the harness applies only that patch to a copied scenario and requires the guided build and runtime to pass.

An empty manifest is meaningful: it says the supported upgrade path is expected to work without consumer source changes. It must never be populated merely to make an unexpected regression green.

## Running locally

```bash
python3 eng/verify-upgrade-compatibility.py validate-config

baseline=$(python3 -c 'import json; print(json.load(open("eng/published-release.json"))["version"])')
target=$(python3 -c 'import json; print(json.load(open("eng/release-manifest.json"))["version"])')

dotnet restore TCJ.slnx --force-evaluate
dotnet build TCJ.slnx -c Release --no-restore
dotnet pack TCJ.slnx -c Release --no-build --output artifacts/upgrade-compatibility/target/packages

python3 upgrade-tests/scripts/run-upgrade-tests.py \
  --baseline-version "$baseline" \
  --target-version "$target" \
  --target-packages artifacts/upgrade-compatibility/target/packages \
  --output artifacts/upgrade-compatibility

python3 eng/verify-upgrade-compatibility.py verify \
  --baseline-version "$baseline" \
  --target-version "$target" \
  --target-packages artifacts/upgrade-compatibility/target/packages \
  --results artifacts/upgrade-compatibility/results \
  --output artifacts/upgrade-compatibility/report
```

Use `--scenario <name>` on the runner for one scenario. The full suite is the release gate.

## Release and post-publication use

Normal CI validates the policy and fixture wiring. The dedicated workflow runs the full before/after suite. Release preflight and the tag workflow run it against the exact release-candidate packages and block readiness/publication on failure. After publication, the published-package workflow reruns Core, ASP.NET Core, and FullStack upgrades with both baseline and target TCJ versions sourced from NuGet.org, checking that publication did not introduce a difference hidden by the local feed.

## Adding or excluding scenarios

Add a minimal external-consumer project, expected behavior fixture, and policy entry. Do not add a production `ProjectReference`. Changes that weaken a tested upgrade guarantee, classify environment noise, exclude a framework/platform, or introduce a migration patch require explicit review and documentation; a broad normalization rule is not an acceptable workaround for flaky behavior.
