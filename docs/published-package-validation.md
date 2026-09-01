# Published-package validation

Source builds prove that the repository compiles. Published-package smoke tests prove that consumers can discover and use the exact artifacts served by NuGet.org.

## What is verified

The `Published package smoke tests` workflow performs independent publication, package-content, feature-smoke, consumer-compatibility, and upgrade-compatibility checks.

### NuGet publication verification

`eng/verify-published-packages.py`:

- uses `eng/published-release.json` as the immutable package inventory for the recorded published baseline;
- when the explicitly requested version matches `eng/release-manifest.json`, uses that current release inventory so newly introduced packages are included in the immediate post-publication verification;
- keeps the published-manifest reader backward compatible with the legacy `packages` schema while the tooling-aware schema records separate `runtime` and `tooling` package sets;
- confirms the exact version exists in the NuGet V3 flat container;
- confirms registration metadata reports the version as listed;
- rejects the NuGet.org unlisted timestamp convention;
- downloads every `.nupkg` from NuGet.org;
- validates ID, version, repository metadata, the release-specific license expression, README, and license file for every release package;
- requires runtime packages to contain their expected `net10.0` assembly content;
- validates tooling packages against the manifest-declared analyzer asset path and rejects forbidden runtime asset prefixes such as `lib/` and `runtime/`;
- for package versions from `0.1.0-preview.3` onward, validates the package README Markdown policy;
- when verifying the current release target after publication, confirms the packed README bytes match the package-specific source under `docs/nuget/`.

The immutable `0.1.0-preview.2` packages predate the package-specific README policy, so the verifier continues to accept their already-published README bytes while enforcing the corrected policy for `0.1.0-preview.3` and later versions. The `0.1.0-preview.3` published metadata remains truthful when represented by the tooling-aware schema: its runtime package set is unchanged and its tooling package set is empty because `TCJ.Generators` was not part of that release.

### Published feature smoke

`smoke/TCJ.PublishedPackages.SmokeTest` is intentionally excluded from `TCJ.slnx`. It references all five TCJ packages rather than repository project references.

Two NuGet configurations have deliberately different responsibilities:

- `smoke/NuGet.Config` maps `TCJ.*` to the local `artifacts/packages` feed and is reserved for packed-candidate Native AOT validation;
- `smoke/NuGet.Published.Config` contains only NuGet.org and is used whenever the published-package consumer is restored.

The workflow performs an explicit restore with `smoke/NuGet.Published.Config`, then executes with `--no-restore`. This prevents the implicit `dotnet run` restore from resolving `smoke/NuGet.Config` and requiring a local candidate feed that does not exist in post-publication jobs.

The executable validates core Result and UUID behavior, dependency-injection registration, ASP.NET Core middleware registration, Entity Framework Core repository registration, SQL Server provider model construction, and assembly loading. For the current published baseline and current release target, it also enables the maintained resilience and health-check smoke paths and enables the SQL Server transactional-outbox smoke on Linux.

### Cross-platform package consumers

The maintained clean-room compatibility consumers restore the published version from NuGet.org and run on:

- `ubuntu-latest`
- `windows-latest`
- `macos-latest`

The selected Core, ASP.NET Core, and full-stack consumers verify exact package source/version identity and runtime behavior. On Linux, the workflow also runs the selected published upgrade scenarios when the requested published target is newer than `eng/published-release.json`.

## Run from GitHub

Open:

```text
Actions → Published package smoke tests → Run workflow
```

Leave `version` empty to use `eng/published-release.json`, or enter an exact published version such as:

```text
0.1.0-preview.4
```

The workflow also runs weekly. It is not a required pull-request check because it depends on the external NuGet.org service and validates immutable published artifacts.

## Run locally

Verify publication metadata and download the packages:

```bash
python3 eng/verify-published-packages.py \
  --version 0.1.0-preview.4
```

Restore the published-package consumer explicitly from NuGet.org:

```bash
dotnet restore \
  smoke/TCJ.PublishedPackages.SmokeTest/TCJ.PublishedPackages.SmokeTest.csproj \
  --configfile smoke/NuGet.Published.Config \
  --force \
  --no-cache \
  -p:TCJPackageVersion=0.1.0-preview.4
```

Then run without another implicit restore:

```bash
dotnet run \
  --project smoke/TCJ.PublishedPackages.SmokeTest/TCJ.PublishedPackages.SmokeTest.csproj \
  --configuration Release \
  --no-restore \
  -p:TCJPackageVersion=0.1.0-preview.4
```

Do not replace `smoke/NuGet.Config` with the published configuration. The local configuration is part of the Native AOT release contract and must continue to source `TCJ.*` from the exact packed candidate.

## Symbols and Source Link

The release pipeline validates portable PDB files before publication. After publication, confirm Source Link manually in a debugger by stepping into at least one TCJ method with symbol loading enabled. Symbol-server propagation is separate from primary-package indexing and is not treated as a restore prerequisite.
