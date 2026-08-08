# SQL Server integration testing

`TCJ.EntityFrameworkCore.SqlServer` is validated against a real Microsoft SQL Server engine through Testcontainers for .NET. EF Core InMemory and SQLite are useful for other test shapes, but they do not validate SQL Server provider registration, migrations, SQL Server constraints, rowversion concurrency, transaction semantics, precision, or actual connectivity.

## Prerequisites

Local execution requires:

- the .NET SDK selected by `global.json`;
- Docker Engine or Docker Desktop;
- Linux-container support;
- enough Docker memory to start SQL Server;
- network access for the first pull of the pinned SQL Server image.

The tests do not use a developer-installed SQL Server instance and do not require a repository or user secret containing a database password. Docker hosts on Linux, Windows, and macOS can run the suite when they support the pinned Linux SQL Server image.

Validate Docker before starting:

```bash
docker version
docker info
```

## Pinned image and policy

The executable policy is tracked in `eng/sqlserver-integration-policy.json`. The SQL Server image is pinned there to:

```text
mcr.microsoft.com/mssql/server:2022-CU14-ubuntu-22.04
```

Floating tags such as `latest` are rejected by `eng/verify-sqlserver-integration.py`. Treat an image update as a dependency and compatibility change: update the policy deliberately, run the complete integration workflow, and review migration, transaction, storage, and concurrency behavior before merging.

Validate repository wiring without starting Docker:

```bash
python3 eng/verify-sqlserver-integration.py validate-config
```

## Credentials and isolation

The collection fixture creates a disposable SQL Server container and generates an SA password at runtime with a cryptographic random generator. The password exists only for the lifetime of that container. `SA_PASSWORD`, `SQL_CONNECTION_STRING`, and `DATABASE_PASSWORD` are not required as permanent repository secrets.

One SQL Server container is shared by the xUnit collection to avoid repeatedly booting the engine. Every test receives a uniquely named database, applies the test migration, and drops the database during disposal. Tests therefore do not depend on execution order or shared database state.
Tests in that collection are intentionally serialized around the single disposable engine to avoid container/resource contention; this is collection-scoped rather than a repository-wide parallelization restriction.

Failure diagnostics include the container stdout/stderr, SQL Server `/var/opt/mssql/log/errorlog`, test-host output, Docker information, and a sanitized environment summary. The generated password and password-bearing connection-string fields are redacted before files are written, and generated results are scanned again for credential patterns by the verifier.

## Schema and migrations

The integration project owns a deterministic test-only migration for its representative model. Each isolated database is created against the real SQL Server container and migrated with `Database.MigrateAsync()` before the test body runs. The migration includes checked-in target-model metadata and a model snapshot, and the migration test asserts that EF Core reports no pending model changes.

The model covers the framework behaviors needed by this gate, including:

- repository CRUD and specifications;
- Unit of Work commit and rollback;
- auditing and soft delete;
- explicit domain-event persistence behavior;
- GUID values, decimal precision, `DateTimeOffset`, nullability, string length, unique indexes, and foreign keys;
- identity generation;
- SQL Server rowversion concurrency.

Domain-event dispatch remains an application-boundary concern in the current framework contract. Persistence must not silently dispatch or clear pending domain events.

## Run locally

Restore and build only the integration project:

```bash
dotnet restore \
  tests/TCJ.EntityFrameworkCore.SqlServer.IntegrationTests/TCJ.EntityFrameworkCore.SqlServer.IntegrationTests.csproj

dotnet build \
  tests/TCJ.EntityFrameworkCore.SqlServer.IntegrationTests/TCJ.EntityFrameworkCore.SqlServer.IntegrationTests.csproj \
  --configuration Release \
  --no-restore
```

Run the complete SQL Server category and write TRX results:

```bash
dotnet test \
  tests/TCJ.EntityFrameworkCore.SqlServer.IntegrationTests/TCJ.EntityFrameworkCore.SqlServer.IntegrationTests.csproj \
  --configuration Release \
  --no-build \
  --filter "Category=SqlServer" \
  --logger "trx;LogFileName=sqlserver-integration.trx" \
  --results-directory TestResults/SqlServerIntegration
```

A narrower category can be selected while investigating a scenario, for example:

```bash
dotnet test \
  tests/TCJ.EntityFrameworkCore.SqlServer.IntegrationTests/TCJ.EntityFrameworkCore.SqlServer.IntegrationTests.csproj \
  --configuration Release \
  --filter "Category=Migration"
```

Verify the complete result set and generate the policy summary:

```bash
python3 eng/verify-sqlserver-integration.py verify \
  --results TestResults/SqlServerIntegration \
  --output artifacts/sqlserver-integration
```

Normal repository CI excludes `Category=SqlServer` from the general solution test command so a database container is not started twice. The dedicated SQL Server workflow owns the real-database run.

## Diagnostics

Generated outputs are local/CI artifacts and must not be committed:

```text
TestResults/SqlServerIntegration/
artifacts/sqlserver-integration/SQLSERVER_INTEGRATION_SUMMARY.md
artifacts/sqlserver-integration/sqlserver-integration-summary.json
artifacts/sqlserver-integration/logs/
```

The test fixture writes sanitized runtime and container diagnostics under `TestResults/SqlServerIntegration/diagnostics/`. The verifier produces the Markdown and JSON summaries under `artifacts/sqlserver-integration/` and rejects failed tests, skipped critical tests, insufficient test count, missing runtime evidence, image-policy mismatches, or detected credential leakage.

If startup fails, first inspect `docker version` and `docker info`, confirm Linux containers are enabled and enough resources are available, then review the sanitized container/startup logs. Do not work around startup failures by switching to an external database or by committing a connection string.

## CI and release gates

`.github/workflows/sqlserver-integration.yml` runs the real SQL Server suite for relevant pull requests, pushes to `main` and `develop`, manual dispatches, and its scheduled run. The workflow validates Docker, policy, restore/build, tests, result count, credential sanitation, summaries, and cleanup; it uploads TRX and diagnostic artifacts.

Normal CI always runs `python3 eng/verify-sqlserver-integration.py validate-config`. Release preflight and the tag-based release workflow call the same SQL Server integration workflow before continuing, so a failing database gate blocks release readiness and NuGet publication from that source commit.
