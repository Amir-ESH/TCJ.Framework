# Package consumer compatibility

Repository tests prove that source projects work together. They do **not** prove that an external application can restore the published package graph. Project references can hide missing NuGet dependencies, bad package metadata, incorrect runtime assets, target-framework mistakes, or source/symbol packaging defects. Step 37 therefore validates TCJ through clean package-only applications.

## What is supported

The compatibility policy is tracked in [`eng/compatibility-policy.json`](https://github.com/Amir-ESH/TCJ.Framework/blob/develop/eng/compatibility-policy.json). The required matrix currently is:

- target framework: `net10.0`;
- architecture: x64 on `ubuntu-latest` and `windows-latest`, arm64 on `macos-latest`;
- operating systems: `ubuntu-latest`, `windows-latest`, and `macos-latest`;
- configuration: Release;
- packages: all five TCJ packages.

The architecture claim is intentionally platform-specific: the required standard GitHub-hosted macOS runner is arm64, while the required Ubuntu and Windows runners are x64. The matrix does not imply that every operating system is validated on every architecture. A new framework, platform, or architecture combination must be added to policy and pass the matrix before documentation advertises it.

## Approved package combinations

The six maintained consumers cover these supported combinations:

1. `TCJ.Core`;
2. `TCJ.Core` + `TCJ.DependencyInjection`;
3. `TCJ.Core` + `TCJ.DependencyInjection` + `TCJ.EntityFrameworkCore`;
4. the previous set + `TCJ.EntityFrameworkCore.SqlServer`;
5. `TCJ.Core` + `TCJ.DependencyInjection` + `TCJ.AspNetCore`;
6. all five packages together.

The consumer projects live under [`compatibility/Consumers/`](https://github.com/Amir-ESH/TCJ.Framework/tree/develop/compatibility/Consumers) and are intentionally not part of the main production solution. They must never reference a project below `src/`.

## Local package source and restore isolation

The dedicated [`compatibility/NuGet.Config`](https://github.com/Amir-ESH/TCJ.Framework/blob/develop/compatibility/NuGet.Config) uses NuGet package-source mapping. `TCJ.*` is mapped only to `artifacts/compatibility/packages`; NuGet.org uses the fallback `*` mapping for public direct and transitive dependencies. Because `TCJ.*` is the more-specific package-source pattern and exists only on `tcj-local`, TCJ packages remain pinned to the local candidate feed; a missing candidate cannot silently fall back to a previously published build.

The runner also isolates:

- `NUGET_PACKAGES` under `artifacts/compatibility/nuget/<platform>/packages`;
- the NuGet HTTP cache;
- `DOTNET_CLI_HOME`;
- every consumer `bin` and `obj` directory.

`project.assets.json` is inspected after restore. Every expected TCJ package must resolve as a package, at the exact candidate version, with no project references and through the isolated cache. The runner then reads NuGet's `.nupkg.metadata` from that isolated cache and verifies the actual source recorded for every TCJ package: the repository-local candidate feed in local mode, or NuGet.org in published mode.

## What each consumer proves

`Core.Console` exercises Result success/failure behavior, guards, extensions, `TimeProvider`, and the GUID generator.

`DependencyInjection.Console` scans its own external assembly and verifies transient, scoped, singleton, self-registration, duplicate registration protection, framework services, and package dependency closure.

`EntityFrameworkCore.Console` uses the public InMemory provider only as a lightweight wiring target. It resolves a repository and `IUnitOfWork`, persists data, applies auditing, executes a specification, and verifies the soft-delete query filter. Real SQL Server behavior remains the responsibility of the Testcontainers suite.

`EntityFrameworkCore.SqlServer.Console` configures SQL Server and resolves the provider and Unit of Work without opening a network connection. This catches missing transitive provider dependencies while keeping the compatibility matrix self-contained.

`AspNetCore.MinimalApi` starts Kestrel on loopback, runs `AddTcjAspNetCore`/`UseTcjAspNetCore`, resolves current-user services, and performs successful and handled-error HTTP requests.

`FullStack.MinimalApi` restores all five packages together, configures DI, ASP.NET Core, EF Core, and SQL Server, and performs an HTTP request that resolves representative TCJ services. This is the ambiguity/conflict check for the complete package graph.

## Package, symbol, and Source Link validation

`eng/verify-consumer-compatibility.py` verifies the exact five `.nupkg` and five `.snupkg` files. Primary packages must contain the expected `lib/<tfm>` assembly and XML documentation, repository metadata, README and license, declared TCJ dependency graph, and no test/sample/build-output or absolute machine paths.

Every symbol package must contain a portable PDB (`BSJB` signature). Source Link metadata is extracted from the PDB and must identify the TCJ repository and, in CI/release workflows, the exact `RepositoryCommit` passed during packing. This complements the separate reproducible-build comparison rather than weakening it.

## Warning and dependency-conflict policy

Consumer projects use `TreatWarningsAsErrors`. The runner also treats any warning observed in restore, build, or runtime command logs as a compatibility failure. This makes package downgrade, incompatible asset, obsolete fixture usage, and other NuGet/compiler warnings blocking instead of informational.

When a dependency conflict occurs, inspect the matching files under:

```text
artifacts/compatibility/results/<platform>/restore/
artifacts/compatibility/results/<platform>/build/
```

Then inspect the consumer's generated `obj/project.assets.json` to see the selected package graph. Do not add broad warning suppressions; fix package metadata or document a narrowly justified policy change.

## Local commands

Validate only configuration:

```bash
python3 eng/verify-consumer-compatibility.py validate-config
```

Pack the current candidate:

```bash
version="$(python3 -c 'import xml.etree.ElementTree as ET; print(ET.parse("eng/Packaging.props").getroot().findtext("./PropertyGroup/Version"))')"

dotnet restore TCJ.slnx --force-evaluate
dotnet build TCJ.slnx --configuration Release --no-restore
dotnet pack TCJ.slnx \
  --configuration Release \
  --no-build \
  --no-restore \
  -p:PackageOutputPath="$(pwd)/artifacts/compatibility/packages" \
  -p:RepositoryCommit="$(git rev-parse HEAD)"
```

Run one Linux-equivalent local matrix pass and verify it:

```bash
python3 compatibility/scripts/run-compatibility.py \
  --version "$version" \
  --platform ubuntu-latest \
  --packages artifacts/compatibility/packages \
  --results artifacts/compatibility/results

python3 eng/verify-consumer-compatibility.py verify \
  --version "$version" \
  --packages artifacts/compatibility/packages \
  --results artifacts/compatibility/results \
  --output artifacts/compatibility/report \
  --platform ubuntu-latest \
  --commit-sha "$(git rev-parse HEAD)"
```

Run a single consumer by appending, for example:

```text
--consumer AspNetCore.MinimalApi
```

The full three-OS matrix is a GitHub Actions responsibility because a single developer machine cannot truthfully claim all three hosted-runner environments.

## CI and release behavior

The dedicated **Package consumer compatibility** workflow runs on relevant pull requests, pushes to `main`/`develop`, manual dispatch, and a weekly schedule. Linux, Windows, and macOS produce independent results, then an aggregate job rejects any missing or failed platform.

Normal CI runs `validate-config`. Release preflight and the tagged release both depend on the three-platform compatibility gate. After reproducible Build A is promoted, they additionally copy the **exact** candidate package set into the compatibility local feed and execute/verify the consumers against those bytes before readiness/publication can continue.

After NuGet publication, **Published package smoke tests** reuse `Core.Console`, `AspNetCore.MinimalApi`, and `FullStack.MinimalApi` through [`compatibility/NuGet.Published.Config`](https://github.com/Amir-ESH/TCJ.Framework/blob/develop/compatibility/NuGet.Published.Config). The existing published-package verifier first checks NuGet.org registration and primary package metadata against `eng/published-release.json`; the reused consumers then prove the public packages restore and execute.

## Adding a consumer scenario

1. Add a minimal project under `compatibility/Consumers/` with package references only.
2. Add it to `compatibility/TCJ.Compatibility.slnx` and `eng/compatibility-policy.json`.
3. Keep the expected console output deterministic.
4. Add only third-party dependencies genuinely required to simulate the external application.
5. Run configuration validation and a local compatibility pass.
6. Let all three hosted operating systems pass before checking the acceptance criterion.

## Proposing an exclusion

A platform, package combination, or target-framework exclusion must describe the external limitation, demonstrate why it is intentional, update the compatibility policy and documentation together, and receive explicit review. A failing matrix is not by itself justification for weakening support claims.
