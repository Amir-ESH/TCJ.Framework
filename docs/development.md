# Development workflow

## Prerequisites

- Git
- .NET SDK selected by `global.json`
- SQL Server LocalDB or another SQL Server instance for the sample

## Restore, build, and test

```bash
python3 eng/verify-dependency-security.py
dotnet restore TCJ.slnx
dotnet build TCJ.slnx -c Release --no-restore
dotnet test TCJ.slnx -c Release --no-build
```

Run coverage:

```bash
dotnet test TCJ.slnx \
  -c Release \
  --collect:"XPlat Code Coverage" \
  --settings tests/coverlet.runsettings
```

Pack locally:

```bash
dotnet pack TCJ.slnx -c Release --no-build
```

Packages are written to `artifacts/packages`. The Pack target also runs SDK package validation against the version in `eng/PackageValidation.props`. CI generates and verifies `artifacts/release/SHA256SUMS` for the complete package set; local contributors can run the same checks with `eng/release-integrity.py`.

## Continuous integration

`.github/workflows/ci.yml` runs for pushes and pull requests targeting `main` or `develop`. It validates dependency-security and release-integrity automation, audits direct and transitive packages during restore, builds, tests, packs, checks binary compatibility against the published package baseline, verifies the complete package set and its SHA-256 manifest, and uploads test and NuGet artifacts.

`.github/workflows/dependency-review.yml` rejects pull requests that introduce moderate-or-higher vulnerable dependencies. `.github/workflows/dependency-audit.yml` performs a scheduled full restore audit so advisories published after the last code change are still detected.

Release publication is isolated in `.github/workflows/release.yml` and is triggered only by `v*` tags. See [Release automation](releasing.md).

Dependabot checks NuGet and GitHub Actions dependencies weekly and targets update pull requests to `develop`.

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
- No secrets, generated outputs, or IDE user settings are committed.

## Generated and local-only paths

Do not commit:

```text
bin/
obj/
artifacts/
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

`eng/release-integrity.py` validates the release workflow configuration and generates or verifies the package checksum manifest. Official tag builds additionally create GitHub artifact attestations. See [Release integrity and build provenance](release-integrity.md).
