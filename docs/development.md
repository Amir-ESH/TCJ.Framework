# Development workflow

## Prerequisites

- Git
- .NET SDK selected by `global.json`
- SQL Server LocalDB or another SQL Server instance for the sample
- Docker with Linux-container support for the SQL Server integration suite

## Restore, build, and test

```bash
python3 eng/verify-dependency-security.py
dotnet restore TCJ.slnx
dotnet build TCJ.slnx -c Release --no-restore
dotnet test TCJ.slnx -c Release --no-build --filter "Category!=SqlServer&Category!=AspNetCore&Category!=Concurrency"
```

Run coverage:

```bash
dotnet test TCJ.slnx \
  -c Release \
  --no-build \
  --filter "Category!=SqlServer&Category!=AspNetCore&Category!=Concurrency" \
  --collect:"XPlat Code Coverage" \
  --settings tests/coverlet.runsettings \
  --results-directory TestResults
python3 eng/verify-coverage.py verify
```

Validate mutation-testing automation:

```bash
python3 -m unittest discover --start-directory eng/tests --pattern "test_*.py"
python3 eng/verify-mutation-results.py validate-config
python3 eng/verify-performance-results.py validate-config
python3 eng/verify-architecture-policy.py validate-config
python3 eng/verify-reproducible-build.py validate-config
python3 eng/verify-sbom.py validate-config
python3 eng/verify-sqlserver-integration.py validate-config
```

A pending baseline is allowed during configuration validation. Run Stryker first, generate a candidate, review both HTML reports, and accept the candidate before normal mutation verification can pass.

Run the complete mutation baseline by following [Mutation testing quality gate](mutation-testing.md).

Pack locally:

```bash
dotnet pack TCJ.slnx -c Release --no-build
```

Packages are written to `artifacts/packages`. The Pack target also runs SDK package validation against the version in `eng/PackageValidation.props`. CI generates and validates a CycloneDX SBOM under `artifacts/sbom/`, then generates and verifies `artifacts/release/SHA256SUMS` for the ten package files and SBOM. Local contributors can run the same checks with `eng/generate-sbom.py`, `eng/verify-sbom.py`, and `eng/release-integrity.py`.

## Continuous integration

`.github/workflows/ci.yml` runs for pushes and pull requests targeting `main` or `develop`. It validates dependency-security, release-integrity, coverage, mutation-testing, performance, architecture-policy, SBOM, and reproducibility automation; audits direct and transitive packages during restore; builds; collects and enforces Cobertura line and branch coverage; packs; checks binary compatibility against the published package baseline; verifies the complete package set and its SHA-256 manifest; and uploads test, coverage, and NuGet artifacts.

`.github/workflows/dependency-review.yml` rejects pull requests that introduce moderate-or-higher vulnerable dependencies. `.github/workflows/dependency-audit.yml` performs a scheduled full restore audit so advisories published after the last code change are still detected.

`.github/workflows/mutation-testing.yml` always creates the stable `Mutation testing / Run mutation tests` check for pull requests and pushes, then uses an internal scope detector to avoid expensive work for unrelated changes. It also runs weekly and manually. The workflow uses xUnit v3 through MTP, runs Stryker before enforcing baseline state, publishes a Markdown summary, and uploads HTML, JSON, metadata, logs, and the first-baseline candidate.

Release publication is isolated in `.github/workflows/release.yml` and is triggered only by `v*` tags. See [Release automation](releasing.md).

Dependabot checks NuGet and GitHub Actions dependencies weekly and targets update pull requests to `develop`.


## SQL Server integration tests

The dedicated `TCJ.EntityFrameworkCore.SqlServer.IntegrationTests` project starts a pinned disposable SQL Server container, creates a database per test, applies test migrations, and writes sanitized diagnostics. Run it separately from the normal solution test command:

```bash
python3 eng/verify-sqlserver-integration.py validate-config
dotnet test tests/TCJ.EntityFrameworkCore.SqlServer.IntegrationTests/TCJ.EntityFrameworkCore.SqlServer.IntegrationTests.csproj \
  -c Release \
  --filter "Category=SqlServer" \
  --logger "trx;LogFileName=sqlserver-integration.trx" \
  --results-directory TestResults/SqlServerIntegration
python3 eng/verify-sqlserver-integration.py verify \
  --results TestResults/SqlServerIntegration \
  --output artifacts/sqlserver-integration
```

See [SQL Server integration testing](sqlserver-integration-testing.md) for prerequisites, image policy, credentials, isolation, migrations, diagnostics, and release enforcement.

## Branch model

```text
feature/*, fix/*, docs/*, test/* → develop
develop                              → main
hotfix/*                             → main and develop
```

Create a change branch:

```bash
git switch develop
git pull --ff-only
git switch -c docs/improve-getting-started
```

Use Conventional Commit-style subjects such as:

```text
feat: add provider integration
fix: preserve validation metadata
docs: explain specification tracking
test: cover soft-delete restore
build: update package metadata
ci: add release workflow
```

## Pull-request checklist

- The change is focused and described clearly.
- Public behavior changes include tests.
- Public API changes include documentation updates.
- Package validation passes against the published API baseline.
- Intentional compatibility suppressions are minimal, reviewed, and documented.
- `dotnet build` succeeds in Release configuration.
- `dotnet test` succeeds.
- `python3 eng/verify-architecture-policy.py validate-config` succeeds.
- Architecture tests pass and any module dependency change is documented.
- The coverage quality gate passes and behavior changes include focused tests.
- Relevant foundational changes pass the mutation quality gate and survived mutants are reviewed.
- A generated mutation candidate is accepted only through the documented review command; pending baselines are never used to skip Stryker execution.
- No secrets, generated outputs, or IDE user settings are committed.

## Generated and local-only paths

Do not commit:

```text
bin/
obj/
artifacts/
StrykerOutput/
.tools/
TestResults/
.vs/
.idea/
*.user
*.suo
*.DotSettings.user
```


## Release lifecycle during development

Normal pull requests target the version in `eng/Packaging.props` and `eng/release-manifest.json`. The manifest remains in `development` status with a null release date until a release candidate is frozen. CI validates this state and deeply inspects locally packed artifacts.

The `smoke/TCJ.PublishedPackages.SmokeTest` project is intentionally outside `TCJ.slnx`; it validates immutable NuGet.org artifacts rather than repository project references. Run it through the **Published package smoke tests** workflow or follow [Published-package validation](published-package-validation.md).

## Public API compatibility

Packable projects use the .NET SDK package validator. See [Public API compatibility](api-compatibility.md) for the baseline lifecycle, breaking-change policy, and suppression workflow.

## Dependency security

Repository restore policy is defined by `NuGet.Config` and `eng/DependencySecurity.props`. See [Dependency and supply-chain security](dependency-security.md) for audit thresholds, source mapping, scheduled checks, and advisory handling.

## Release integrity

`eng/release-integrity.py` validates the release workflow configuration and generates or verifies the checksum manifest covering packages and the versioned CycloneDX SBOM. `eng/generate-sbom.py` and `eng/verify-sbom.py` inventory and validate release packages and restored production dependencies. Official tag builds additionally create GitHub artifact attestations for packages, SBOM, and checksums. See [Release integrity and build provenance](release-integrity.md) and [Software bill of materials](software-bill-of-materials.md).

## Code coverage

Coverage policy and merged-report behavior are documented in [Code coverage quality gate](code-coverage.md).

## Performance benchmarks

The benchmark project lives under `benchmarks/TCJ.Benchmarks` and uses BenchmarkDotNet with memory diagnostics and JSON, Markdown, and CSV exporters. Normal CI validates the benchmark configuration; the dedicated **Performance benchmarks** workflow runs a short benchmark job for relevant pull requests and a full job on its weekly schedule or through manual dispatch.

Run the complete suite locally from the repository root:

```bash
dotnet run --project benchmarks/TCJ.Benchmarks/TCJ.Benchmarks.csproj \
  --configuration Release -- --filter "*"
python3 eng/verify-performance-results.py verify
```

Generated output belongs under `artifacts/performance/` and must not be committed. See [`performance-benchmarks.md`](performance-benchmarks.md) for filtering, baseline interpretation, ratio policy, and accepted-regression rules.

## Architecture tests

The architecture-test project is part of `TCJ.slnx`, so normal CI, release preflight, and tagged release tests execute it automatically. Run the category explicitly while changing package boundaries:

```bash
python3 eng/verify-architecture-policy.py validate-config
dotnet test tests/TCJ.Architecture.Tests/TCJ.Architecture.Tests.csproj \
  -c Release \
  -- --filter-trait "Category=Architecture"
```

The approved graph and change process are documented in [`architecture-tests.md`](architecture-tests.md).

## Mutation testing

The initial Stryker.NET baseline, exclusions, local commands, and threshold-update process are documented in [Mutation testing quality gate](mutation-testing.md).


## Reproducible package builds

Run `python3 eng/verify-reproducible-build.py validate-config` before changing packaging, SDK, Source Link, deterministic build properties, or release workflows. The dedicated workflow creates isolated Build A and Build B trees under `artifacts/reproducibility/`, packs all five `.nupkg` and `.snupkg` files twice, and compares the extracted payload plus assemblies, PDBs, Source Link, XML documentation, sources, and NuGet metadata. Use [Reproducible NuGet package builds](reproducible-builds.md) for the full local command sequence, normalization policy, and difference-report investigation process.

## Documentation validation

Restore and invoke the repository-pinned DocFX tool rather than a global installation:

```bash
dotnet tool restore
python3 eng/verify-documentation.py validate-config
dotnet build TCJ.slnx --configuration Release
dotnet docfx metadata docfx/docfx.json --warningsAsErrors
dotnet docfx build docfx/docfx.json --warningsAsErrors
python3 eng/verify-documentation.py verify \
  --configuration Release \
  --build-root src \
  --api-root artifacts/documentation/api \
  --output artifacts/documentation
```

The verifier writes coverage, baseline, missing-documentation, broken-link, snippet, and generated-page results under `artifacts/documentation/`. See [Documentation authoring](documentation-authoring.md) before changing XML comments, package pages, selected examples, or the baseline.

## ASP.NET Core end-to-end integration

The normal coverage run excludes `Category=AspNetCore`; use the dedicated in-memory HTTP suite when changing ASP.NET Core registration, middleware, current-user behavior, or Result-to-HTTP mapping:

```bash
python3 eng/verify-aspnetcore-integration.py validate-config
dotnet test tests/TCJ.AspNetCore.IntegrationTests/TCJ.AspNetCore.IntegrationTests.csproj \
  -c Release --filter "Category=AspNetCore" \
  --logger "trx;LogFileName=aspnetcore-integration.trx" \
  --results-directory TestResults/AspNetCoreIntegration
python3 eng/verify-aspnetcore-integration.py verify \
  --results TestResults/AspNetCoreIntegration \
  --output artifacts/aspnetcore-integration
```

The CI gate executes the suite on Linux and Windows. Test authentication and endpoints remain in the test project, and generated diagnostics are sanitized before artifact upload. See [ASP.NET Core end-to-end integration testing](aspnetcore-integration-testing.md).

## Package consumer compatibility

Package changes must also work as external NuGet dependencies, not only as project references inside `TCJ.slnx`. Validate the tracked matrix first:

```bash
python3 eng/verify-consumer-compatibility.py validate-config
```

After packing a candidate into `artifacts/compatibility/packages`, run the clean-room consumers with `compatibility/scripts/run-compatibility.py` and then `eng/verify-consumer-compatibility.py verify`. The runner isolates `NUGET_PACKAGES`, the HTTP cache, `DOTNET_CLI_HOME`, and consumer `bin`/`obj`; it verifies the exact TCJ version and actual NuGet source recorded in `.nupkg.metadata`, treats restore/build warnings as failures, and executes the HTTP/EF/SQL Server smoke paths. GitHub Actions repeats the full matrix on Linux, Windows, and macOS. See [Package consumer compatibility](package-consumer-compatibility.md) for commands and support rules.

## Package upgrade compatibility

Before changing package-facing behavior, validate the upgrade policy with `python3 eng/verify-upgrade-compatibility.py validate-config`. The full `upgrade-tests/` suite restores the published baseline from NuGet.org, restores the target from locally packed candidate packages, builds and runs the same source, compares dependency graphs and normalized behavior, and verifies explicit migration guidance. See [package upgrade testing](package-upgrade-testing.md).

## Property and fuzz quality gates

Run `python3 eng/verify-fuzzing.py validate-config` when changing Core, dependency registration, property generators, fuzz targets, corpus files, or fuzz policy. Property tests are deterministic and run in normal CI. The dedicated fuzz workflow runs bounded PR campaigns and longer weekly campaigns. See [Property and fuzz testing](property-and-fuzz-testing.md) for replay, corpus, minimization, and local target commands.

## Observability validation

Telemetry changes must keep `eng/observability-policy.json` and `eng/observability-contract.json` aligned with implementation and tests. Run `python3 eng/verify-observability.py validate-config`, then the `TCJ.Observability.Tests` project and `verify-observability.py verify` command documented in [the observability guide](observability.md). Generated `TestResults/Observability/` and `artifacts/observability/` content is local/CI evidence and must not be committed.

## Resilience validation

Resilience changes must keep `eng/resilience-policy.json` and `eng/resilience-contract.json` aligned with production defaults, deterministic tests, telemetry, benchmarks, and automation. Run `python3 eng/verify-resilience.py validate-config`, then `TCJ.Resilience.Tests` with the fast non-SQL filter locally; the dedicated workflow runs provider-specific SQL Server scenarios and verifies generated TRX/trace evidence. Do not commit `TestResults/Resilience/` or `artifacts/resilience/`. See [the resilience guide](resilience.md) before changing retry or transaction boundaries.
