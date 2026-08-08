# TCJ package upgrade tests

This workspace validates the consumer upgrade path from the latest published TCJ release to the release candidate without using production project references.

The baseline version is read from `eng/published-release.json`; the target version is read from `eng/release-manifest.json`. Baseline TCJ packages restore only from NuGet.org. Target TCJ packages restore from the isolated local candidate feed under `artifacts/upgrade-compatibility/target/packages/`, except for the post-publication mode where the target is intentionally restored from NuGet.org.

## Scenarios

- `CoreConsumer`
- `DependencyInjectionConsumer`
- `EntityFrameworkCoreConsumer`
- `EntityFrameworkCore.SqlServerConsumer`
- `AspNetCoreConsumer`
- `FullStackConsumer`

Every scenario uses `$(TCJUpgradeVersion)` for TCJ PackageReferences. The runner hashes the scenario source tree before and after the direct upgrade, records the package source from NuGet's `.nupkg.metadata`, captures `project.assets.json` as a normalized dependency graph, and compares deterministic `behavior.json` output.

`EntityFrameworkCoreConsumer` uses a SQLite file persisted between the baseline and target phases so the target package must read data written while the baseline package was active. The SQL Server scenario validates provider registration and options without opening a database connection; real SQL Server behavior remains covered by the SQL Server integration suite.

## Local run

First pack the target packages:

```bash
dotnet restore TCJ.slnx --force-evaluate
dotnet build TCJ.slnx -c Release --no-restore
dotnet pack TCJ.slnx -c Release --no-build --output artifacts/upgrade-compatibility/target/packages
```

Then resolve the versions from repository metadata and run the suite:

```bash
baseline=$(python3 -c 'import json; print(json.load(open("eng/published-release.json"))["version"])')
target=$(python3 -c 'import json; print(json.load(open("eng/release-manifest.json"))["version"])')

python3 eng/verify-upgrade-compatibility.py validate-config
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

Run one scenario by adding `--scenario CoreConsumer` to the runner. Generated restore caches, build output, behavior files, dependency diffs, and reports are ignored by Git.

## Intentional breaking changes

Do not change a scenario during the direct-upgrade phase. If a deliberate breaking change requires consumer edits, declare it in `eng/breaking-changes.json`, record explicit maintainer approval, link it to an approved issue or PR and a migration-guide heading, and store an explicit patch under the affected scenario's `Migrations/` directory. The harness applies only the declared target-version patch and requires the guided migration to restore, build, and run successfully.
