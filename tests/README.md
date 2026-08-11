# Tests

The repository separates tests by production package:

```text
TCJ.Core.Tests
TCJ.DependencyInjection.Tests
TCJ.EntityFrameworkCore.Tests
TCJ.EntityFrameworkCore.SqlServer.Tests
TCJ.EntityFrameworkCore.SqlServer.IntegrationTests
TCJ.AspNetCore.Tests
TCJ.AspNetCore.IntegrationTests
TCJ.Architecture.Tests
```

The suite uses xUnit v3, Microsoft.NET.Test.Sdk, the Visual Studio runner adapter, and Coverlet collection settings shared through `TestProject.props`.

Run all tests:

```bash
dotnet test TCJ.slnx -c Release --filter "Category!=SqlServer&Category!=AspNetCore&Category!=Concurrency"
```

Run coverage:

```bash
dotnet test TCJ.slnx \
  -c Release \
  --filter "Category!=SqlServer&Category!=AspNetCore&Category!=Concurrency" \
  --collect:"XPlat Code Coverage" \
  --settings tests/coverlet.runsettings \
  --results-directory TestResults
python3 eng/verify-coverage.py verify
```

The verifier merges all package-specific Cobertura reports and enforces the repository thresholds from `eng/coverage-policy.json`. Tests should focus on public behavior and high-risk integration boundaries. Bug fixes should include a regression test in the closest package-specific project.

## Architecture tests

`TCJ.Architecture.Tests` references all five production projects for inspection and enforces the dependency graph, forbidden infrastructure references, namespace ownership, public API boundaries, and stable naming/visibility rules declared in `eng/architecture-policy.json`.

Run only the architecture category:

```bash
dotnet test tests/TCJ.Architecture.Tests/TCJ.Architecture.Tests.csproj \
  -c Release \
  -- --filter-trait "Category=Architecture"
```

Failures identify the assembly or type, the unexpected dependency or namespace, the expected rule, and the policy/documentation that must be updated for an intentional architecture change.

## Mutation testing

`TCJ.Core.Tests` and `TCJ.DependencyInjection.Tests` are the first mutation-testing test projects. Stryker uses the Microsoft Testing Platform runner for xUnit v3 and mutates only the controlled production files listed in `eng/mutation-policy.json`; test code is never mutated.

The verifier rejects missing reports, runner failures, mismatched hashes, excessive compile errors, incomplete statuses, zero-killed/all-survived executions, and score regressions. A pending baseline does not prevent Stryker from running: a valid run produces a candidate, which becomes a recorded baseline only after both HTML reports are reviewed and `accept-baseline` records reviewer identity and notes. See [`docs/mutation-testing.md`](../docs/mutation-testing.md).

## Real SQL Server integration tests

`TCJ.EntityFrameworkCore.SqlServer.IntegrationTests` validates the SQL Server package against a pinned disposable Testcontainers database. Docker with Linux-container support is required; no external database or permanent database password is used.

```bash
python3 eng/verify-sqlserver-integration.py validate-config
dotnet test tests/TCJ.EntityFrameworkCore.SqlServer.IntegrationTests/TCJ.EntityFrameworkCore.SqlServer.IntegrationTests.csproj \
  -c Release \
  --filter "Category=SqlServer" \
  --logger "trx;LogFileName=sqlserver-integration.trx" \
  --results-directory TestResults/SqlServerIntegration
python3 eng/verify-sqlserver-integration.py verify \
  --results TestResults/SqlServerIntegration \
  --output artifacts/sqlserver-integration
```

See [`docs/sqlserver-integration-testing.md`](../docs/sqlserver-integration-testing.md) for the full lifecycle and diagnostics policy.

## ASP.NET Core end-to-end integration tests

`TCJ.AspNetCore.IntegrationTests` starts a real in-memory ASP.NET Core application through TestServer. It validates startup, public TCJ registration, middleware, deterministic authentication, current-user isolation, DI lifetimes, exception mapping, Problem Details, cancellation, and sanitized diagnostics. No deployed server or external network service is required.

```bash
python3 eng/verify-aspnetcore-integration.py validate-config
dotnet test tests/TCJ.AspNetCore.IntegrationTests/TCJ.AspNetCore.IntegrationTests.csproj \
  -c Release \
  --filter "Category=AspNetCore" \
  --logger "trx;LogFileName=aspnetcore-integration.trx" \
  --results-directory TestResults/AspNetCoreIntegration
python3 eng/verify-aspnetcore-integration.py verify \
  --results TestResults/AspNetCoreIntegration \
  --output artifacts/aspnetcore-integration
```

The dedicated GitHub Actions workflow runs the same suite on Linux and Windows and aggregates both results. See [`docs/aspnetcore-integration-testing.md`](../docs/aspnetcore-integration-testing.md).

## Property-based testing

`TCJ.PropertyTests` is the deterministic FsCheck suite for foundational `TCJ.Core` and `TCJ.DependencyInjection` invariants. It is intentionally separate from example-based unit tests, runs at least 100 generated cases per property, pins replay seeds, and uses custom boundary-heavy generators with shrinking. See `docs/property-and-fuzz-testing.md` for local commands and replay guidance.


## Concurrency stress tests

`TCJ.Concurrency.Tests` owns deterministic concurrency contracts. Run the bounded core suite with `dotnet test tests/TCJ.Concurrency.Tests/TCJ.Concurrency.Tests.csproj -c Release --filter "Category=Concurrency&Category!=AspNetCore&Category!=SqlServer"`. Replay an exact failure with `python3 tests/TCJ.Concurrency.Tests/scripts/replay-stress.py --scenario <name> --seed <seed>`. SQL Server scenarios use the pinned Testcontainers image and must not be used to imply that a shared `DbContext` or Unit of Work is thread-safe.

## Observability tests

`TCJ.Observability.Tests` uses in-memory `ActivityListener` and `MeterListener` instances; it requires no collector or network exporter. The suite covers stable source/meter identity and versions, no-listener behavior, parent-child propagation, success/failure/cancellation, domain-event and repository/Unit of Work instrumentation, ASP.NET Core exception handling, metric names/units/dimensions, and synthetic sensitive-data markers. Run the verifier afterward to generate `artifacts/observability/`.

## Health-check integration tests

`TCJ.HealthChecks.Tests` validates liveness/readiness HTTP endpoints, startup diagnostics, duplicate registration, safe JSON, bounded cache/single-flight concurrency, cancellation, telemetry, and SQL Server connectivity/migration state through the pinned Testcontainers image. Run `python3 eng/verify-health-checks.py validate-config` before the suite and `verify` against `TestResults/HealthChecks/` afterward.

## Transactional outbox tests

`TCJ.Outbox.Tests` is the Step 44 SQL Server Testcontainers suite. It covers transactional persistence/rollback, stable message IDs across SaveChanges retry, synchronous/asynchronous capture, manual processing, bounded retry, poison isolation, concurrent workers, lease expiration, cancellation recovery, idempotent duplicate delivery, replay, cleanup, sensitive payload handling, schema/indexes, serialization, and health checks. Use `TestResults/Outbox/` for TRX output and `eng/verify-outbox.py` to generate sanitized evidence.
