# Development workflow

## Prerequisites

- Git
- .NET SDK selected by `global.json`
- SQL Server LocalDB or another SQL Server instance for the sample

## Restore, build, and test

```bash
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

Packages are written to `artifacts/packages`.

## Continuous integration

`.github/workflows/ci.yml` runs for pushes and pull requests targeting `main` or `develop`. It restores, builds, tests, packs, verifies the complete package set, and uploads test and NuGet artifacts.

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
