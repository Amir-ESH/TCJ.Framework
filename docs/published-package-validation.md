# Published-package validation

Source builds prove that the repository compiles. Published-package smoke tests prove that consumers can discover and use the exact artifacts served by NuGet.org.

## What is verified

The `Published package smoke tests` workflow performs two independent layers of validation.

### NuGet publication verification

`eng/verify-published-packages.py`:

- reads the package IDs, default version, and expected SPDX license expression from `eng/published-release.json`;
- confirms the exact version exists in the NuGet V3 flat container;
- confirms registration metadata reports the version as listed;
- rejects the NuGet.org unlisted timestamp convention;
- downloads every `.nupkg` from NuGet.org;
- validates ID, version, repository metadata, the release-specific license expression, README, license file, and `net10.0` assembly contents;
- when the tagged release workflow verifies the just-published candidate before `eng/published-release.json` is advanced, resolves that candidate's expected license expression from `eng/release-manifest.json`.

### Consumer smoke test

`smoke/TCJ.PublishedPackages.SmokeTest` is intentionally excluded from `TCJ.slnx`. It references all five TCJ packages from NuGet.org rather than project references.

The workflow restores, builds, and runs this consumer on:

- `ubuntu-latest`
- `windows-latest`

The executable validates core Result and UUID behavior, dependency-injection registration, ASP.NET Core middleware registration, Entity Framework Core repository registration, SQL Server provider model construction, and assembly loading. It does not connect to a database.

## Run from GitHub

Open:

```text
Actions → Published package smoke tests → Run workflow
```

Leave `version` empty to use `eng/published-release.json`, or enter an exact published version such as:

```text
0.1.0-preview.1
```

The workflow also runs weekly. It is not a required pull-request check because it depends on the external NuGet.org service and tests already-published immutable artifacts.

## Run locally

Verify publication metadata and download the packages:

```bash
python3 eng/verify-published-packages.py \
  --version 0.1.0-preview.1
```

Restore and run the consumer:

```bash
dotnet restore \
  smoke/TCJ.PublishedPackages.SmokeTest/TCJ.PublishedPackages.SmokeTest.csproj \
  --no-cache \
  -p:TCJPackageVersion=0.1.0-preview.1

dotnet run \
  --project smoke/TCJ.PublishedPackages.SmokeTest/TCJ.PublishedPackages.SmokeTest.csproj \
  --configuration Release \
  -p:TCJPackageVersion=0.1.0-preview.1
```

The consumer project pins NuGet.org as its package source and disables central package management locally so the tested version is explicit.

## Symbols and Source Link

The release pipeline validates portable PDB files before publication. After publication, confirm Source Link manually in a debugger by stepping into at least one TCJ method with symbol loading enabled. Symbol-server propagation is separate from primary-package indexing and is not treated as a restore prerequisite.
