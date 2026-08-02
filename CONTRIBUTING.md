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
python3 eng/verify-mutation-results.py validate-config
python3 eng/verify-mutation-results.py validate-baseline
dotnet restore TCJ.slnx
dotnet build TCJ.slnx -c Release --no-restore
dotnet test TCJ.slnx -c Release --no-build \
  --collect:"XPlat Code Coverage" \
  --settings tests/coverlet.runsettings \
  --results-directory TestResults
python3 eng/verify-coverage.py verify
```

When a new mutation baseline is required, capture a candidate, review both HTML reports, then run `verify-mutation-results.py accept-baseline` with reviewer identity and review notes. Never rename or copy an unreviewed candidate directly to `eng/mutation-baseline.json`.

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
- Do not lower the mutation gate, replace a measured baseline with a guessed value, or ignore survived mutants without a reviewed technical justification.
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

Pull requests targeting `develop` or `main` must pass the `Build, test and pack` check and dependency review. Restore audits the complete resolved NuGet graph and fails on moderate-or-higher known vulnerabilities or an unavailable audit source. The Pack phase includes SDK package validation against the latest published TCJ version and rejects accidental binary-breaking API changes. CI also enforces line and branch coverage, requires a recorded mutation baseline, executes the reusable mutation quality gate, and verifies the complete package checksum manifest. Trusted provenance attestations are created only by the official tagged Release workflow. Do not bypass required checks for normal changes. Release tags and NuGet publishing are maintainer-only operations described in [`docs/releasing.md`](docs/releasing.md).

## Pull requests

Target regular changes to `develop`. Release pull requests flow from `develop` to `main`.

A pull request should explain:

- the problem;
- the chosen approach;
- compatibility or migration impact, including any `CPxxxx` suppression;
- tests performed.

By contributing, you agree that your contribution is licensed under the repository's MIT License.
