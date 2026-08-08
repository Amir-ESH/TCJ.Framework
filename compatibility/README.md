# TCJ package consumer compatibility workspace

This workspace behaves like an external application set. It deliberately sits outside `TCJ.slnx` and references TCJ only through NuGet `PackageReference` items. A project reference to `src/` is a compatibility failure.

## Consumer matrix

| Consumer | TCJ packages | Runtime check |
|---|---|---|
| `Core.Console` | `TCJ.Core` | Result, guards, extensions, time/GUID abstractions |
| `DependencyInjection.Console` | Core + DependencyInjection | convention discovery and transient/scoped/singleton/self resolution |
| `EntityFrameworkCore.Console` | Core + DependencyInjection + EntityFrameworkCore | InMemory repository, specification, Unit of Work, auditing, soft delete |
| `EntityFrameworkCore.SqlServer.Console` | Core + DependencyInjection + EntityFrameworkCore + SqlServer | SQL Server registration/options/provider resolution without opening a connection |
| `AspNetCore.MinimalApi` | Core + DependencyInjection + AspNetCore | Kestrel startup, current user, success and handled-error HTTP requests |
| `FullStack.MinimalApi` | all five packages | all modules restored and resolved together plus HTTP/EF/SQL Server wiring |

The required target framework is defined by `eng/compatibility-policy.json`; consumers read it through `TCJCompatibilityTargetFramework`. The current required framework is `net10.0`.

## Package sources

`NuGet.Config` maps `TCJ.*` **only** to `../artifacts/compatibility/packages`. NuGet.org uses the fallback `*` mapping for public direct and transitive dependencies, while the more-specific `TCJ.*` mapping points only to the local feed. NuGet package-source mapping specificity therefore keeps TCJ packages on the local source; if a candidate package is absent, restore fails instead of silently falling back to NuGet.org.

`NuGet.Published.Config` is used only by the post-publication workflow and maps the same consumers to NuGet.org.


## Architecture matrix

The required hosted-runner combinations are x64 on `ubuntu-latest` and `windows-latest`, and arm64 on `macos-latest`. The verifier checks the architecture reported by each platform result instead of making a blanket cross-platform architecture claim.

## Local run

From the repository root:

```bash
python3 eng/verify-consumer-compatibility.py validate-config

version="$(python3 -c 'import xml.etree.ElementTree as ET; print(ET.parse("eng/Packaging.props").getroot().findtext("./PropertyGroup/Version"))')"

dotnet restore TCJ.slnx --force-evaluate
dotnet build TCJ.slnx --configuration Release --no-restore

dotnet pack TCJ.slnx \
  --configuration Release \
  --no-build \
  --no-restore \
  -p:PackageOutputPath="$(pwd)/artifacts/compatibility/packages" \
  -p:RepositoryCommit="$(git rev-parse HEAD)"

python3 compatibility/scripts/run-compatibility.py \
  --version "$version" \
  --platform ubuntu-latest \
  --configuration Release \
  --source-mode local \
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

To run one fixture, add `--consumer Core.Console` (or another policy consumer name) to `run-compatibility.py`.

Generated restore caches, builds, runtime logs, packages, and reports belong under `artifacts/compatibility/` or consumer `bin`/`obj` directories and are never committed.
