# Contributing to TCJ Framework

Thank you for helping improve TCJ Framework.

## Before opening work

- Search existing issues and pull requests.
- Open an issue before large API or architectural changes.
- Keep changes focused on one concern.
- Do not include secrets, generated packages, build output, or personal IDE settings.

## Development setup

```bash
git clone https://github.com/Amir-ESH/TCJ.Framework.git
cd TCJ.Framework
python3 eng/verify-dependency-security.py
python3 eng/release-integrity.py validate-config
python3 eng/verify-coverage.py validate-config
python3 -m unittest discover --start-directory eng/tests --pattern "test_*.py"
python3 eng/verify-mutation-results.py validate-config
python3 eng/verify-performance-results.py validate-config
python3 eng/verify-architecture-policy.py validate-config
python3 eng/verify-sbom.py validate-config
python3 eng/verify-reproducible-build.py validate-config
dotnet restore TCJ.slnx
dotnet build TCJ.slnx -c Release --no-restore
dotnet test TCJ.slnx -c Release --no-build \
  --collect:"XPlat Code Coverage" \
  --settings tests/coverlet.runsettings \
  --results-directory TestResults
python3 eng/verify-coverage.py verify
```

For the first mutation baseline, run the dedicated workflow in `capture-baseline` mode, review both HTML reports, and accept the generated candidate with reviewer identity and notes. A pending baseline must not be used as a precondition that prevents Stryker from running; the complete process is documented in [`docs/mutation-testing.md`](docs/mutation-testing.md).

The Product API sample requires SQL Server. Its default Development configuration uses LocalDB on Windows.

## Branches

Create normal work from `develop`:

```bash
git switch develop
git pull --ff-only
git switch -c feature/short-description
```

Use prefixes such as `feature/`, `fix/`, `docs/`, `test/`, `build/`, and `chore/`.

## Code changes

- Follow `.editorconfig`.
- Preserve nullable-reference-type correctness.
- Prefer explicit behavior over hidden conventions.
- Keep `TCJ.Core` free from ASP.NET Core and EF Core dependencies.
- Add or update XML documentation for public APIs.
- Preserve binary compatibility with the published package baseline unless a breaking change is explicitly approved and documented.
- Do not disable NuGet Audit, weaken the audit threshold, or add an unreviewed package source.
- Do not weaken checksum or attestation enforcement in the tagged Release workflow.
- Add focused tests for bug fixes and public behavior changes.
- Do not weaken coverage thresholds or exclusions without a documented technical reason.
- Do not lower mutation thresholds, accept an unreviewed candidate, or add broad mutation exclusions merely to make CI green.
- Do not raise performance thresholds, remove benchmark categories, or exclude regressions without a documented technical justification.
- Never commit `BenchmarkDotNet.Artifacts/`, `artifacts/performance/`, `artifacts/sbom/`, `artifacts/reproducibility/`, or generated `*.cdx.json` files.
- Do not weaken SBOM package, hash, license, dependency, repository, or provenance metadata requirements merely to make CI green.
- Do not weaken deterministic-build settings or add broad reproducibility normalizations merely to make package comparison pass.
- Package-layout, SDK, compiler, Source Link, or reproducibility-policy changes require focused review and a successful full double build.
- Treat `eng/architecture-policy.json` as executable design documentation; dependency-direction changes require architectural justification.
- Do not weaken, suppress, or broadly exclude architecture rules merely to make CI green.
- Update relevant pages under `docs/`.

## Commit messages

Use a concise Conventional Commit-style subject:

```text
feat: add specification projection
fix: preserve domain event order
docs: explain SQL Server retries
test: cover current user resolution
```

## Automated validation

Pull requests targeting `develop` or `main` must pass the `Build, test and pack` check and dependency review. Restore audits the complete resolved NuGet graph and fails on moderate-or-higher known vulnerabilities or an unavailable audit source. The Pack phase includes SDK package validation against the latest published TCJ version and rejects accidental binary-breaking API changes. CI also enforces the line and branch coverage policy and verifies the complete package checksum manifest. The separate `Mutation testing / Run mutation tests` check must pass for mutation-relevant changes; during the one-time baseline bootstrap, a candidate must be reviewed and committed before normal verification can pass. The separate `Performance benchmarks / Run benchmarks` workflow executes a short job for relevant pull requests and full jobs for scheduled or manual runs; it preserves within-run measurements and policy results for review. Architecture tests run inside the normal solution test command and enforce module dependency directions, namespace ownership, and public API boundaries from `eng/architecture-policy.json`. CI also generates and verifies a CycloneDX release SBOM from locally packed artifacts and restored production dependencies; policy changes, missing license data, or dependency-graph changes require explicit review. The separate `Reproducible builds / Compare package builds` workflow performs isolated Build A and Build B package production for relevant changes and scheduled/manual validation. Extracted package, assembly, PDB, Source Link, XML documentation, source, and NuGet metadata differences are blocking; raw ZIP-only differences are reported under the narrow documented policy. Trusted provenance attestations are created only by the official tagged Release workflow after the verified Build A package set is promoted. Do not bypass required checks for normal changes. Release tags and NuGet publishing are maintainer-only operations described in [`docs/releasing.md`](docs/releasing.md).

## Pull requests

Target regular changes to `develop`. Release pull requests flow from `develop` to `main`.

A pull request should explain:

- the problem;
- the chosen approach;
- compatibility or migration impact, including any `CPxxxx` suppression;
- tests performed;
- architecture-policy and documentation changes when module boundaries are intentionally changed;
- reproducibility-policy or normalization justification when build/package comparison behavior changes.

By contributing, you agree that your contribution is licensed under the repository's MIT License.
